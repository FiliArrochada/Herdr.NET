using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HerdrNet.Transport;

/// <summary>Opens the duplex byte stream a <see cref="HerdrConnection"/> runs over.</summary>
public interface IHerdrTransport
{
    /// <summary>For logs and messages: where this transport goes.</summary>
    string Description { get; }

    /// <summary>Opens a fresh stream. Each connection (and each reconnect) opens its own.</summary>
    ValueTask<Stream> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Connects straight to a core's Unix socket or named pipe.</summary>
public sealed class EndpointTransport(HerdrEndpoint endpoint, TimeSpan? connectTimeout = null) : IHerdrTransport
{
    /// <summary>Where it connects.</summary>
    public HerdrEndpoint Endpoint { get; } = endpoint;

    /// <inheritdoc />
    public string Description => Endpoint.Address;

    /// <inheritdoc />
    public async ValueTask<Stream> OpenAsync(CancellationToken cancellationToken = default)
    {
        var timeout = connectTimeout ?? TimeSpan.FromSeconds(2);
        if (Endpoint.Kind == HerdrEndpointKind.NamedPipe)
        {
            var pipe = new NamedPipeClientStream(".", Endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (TimeoutException e)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw new HerdrCoreUnavailableException(Endpoint.Address, e);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Endpoint.Address), timeoutCts.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new HerdrCoreUnavailableException(Endpoint.Address, e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Runs <c>hc connect</c> and speaks the protocol over its stdin and stdout: no endpoint rules to
/// follow, and the same works through <c>ssh host hc connect</c> when given that command.
/// </summary>
public sealed class HcConnectTransport : IHerdrTransport
{
    readonly string _executable;
    readonly IReadOnlyList<string> _arguments;
    readonly string? _session;
    readonly ILogger _logger;

    /// <summary>Runs <c><paramref name="hcPath"/> connect</c>, for <paramref name="session"/> when given.</summary>
    public HcConnectTransport(string hcPath, string? session = null, ILogger? logger = null)
        : this(hcPath, ["connect"], session, logger)
    {
        Description = $"{hcPath} connect";
    }

    /// <summary>
    /// Runs any command that carries the protocol on its stdio, such as <c>ssh box hc connect</c>. Its
    /// <see cref="Description"/> names only the program: arguments can hold key paths, hosts or worse,
    /// and the description goes into logs and error messages.
    /// </summary>
    public HcConnectTransport(string executable, IReadOnlyList<string> arguments, string? session = null, ILogger? logger = null)
    {
        _executable = executable;
        _arguments = arguments;
        _session = session;
        _logger = logger ?? NullLogger.Instance;
        Description = $"{Path.GetFileName(executable)} (a command)";
    }

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    public ValueTask<Stream> OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(_executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in _arguments)
            start.ArgumentList.Add(argument);
        if (_session is not null)
            start.Environment["HC_SESSION"] = HerdrEndpoint.SessionName(_session);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new HerdrConnectionException($"Could not start {Description}.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new HerdrConnectionException($"Could not start {Description}: {e.Message}", e);
        }

        var stream = new ProcessStdioStream(process);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
            {
                stream.RecordStderr(line);
                _logger.LogWarning("{Transport}: {Line}", Description, line);
            }
        };
        process.BeginErrorReadLine();
        return ValueTask.FromResult<Stream>(stream);
    }
}

/// <summary>Uses a stream someone else opened, once. For tests and custom carriers.</summary>
public sealed class StreamTransport(Stream stream, string description = "stream") : IHerdrTransport
{
    int _used;

    /// <inheritdoc />
    public string Description => description;

    /// <inheritdoc />
    public ValueTask<Stream> OpenAsync(CancellationToken cancellationToken = default) =>
        Interlocked.Exchange(ref _used, 1) == 0
            ? ValueTask.FromResult(stream)
            : throw new InvalidOperationException("A StreamTransport opens its stream only once.");
}

/// <summary>Finds an installed <c>hc</c>.</summary>
public static class HcLocator
{
    /// <summary>
    /// <paramref name="configured"/> when it exists; else <c>hc</c> or <c>herdr-core</c> on PATH;
    /// else the Windows installer's folder. Null when none is found.
    /// </summary>
    public static string? Find(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;

        var windows = OperatingSystem.IsWindows();
        string[] names = windows ? ["hc.exe", "herdr-core.exe"] : ["hc", "herdr-core"];
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        if (windows && Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
        {
            var installed = Path.Combine(local, "Programs", "herdr-core", "herdr-core.exe");
            if (File.Exists(installed))
                return installed;
        }

        return null;
    }
}
