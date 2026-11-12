using HerdrNet.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HerdrNet;

/// <summary>Which way a <see cref="ConnectionSupervisor"/> reaches the core.</summary>
public enum HerdrTransportPreference
{
    /// <summary>
    /// The core's socket or pipe; when nothing listens there, <c>hc connect</c>, which starts the core. Only
    /// until the first connection: after a loss nothing listening means the core was stopped, and starting
    /// it again is the person's call (<see cref="HerdrConnectionState.CoreStopped"/>, <see cref="ConnectionSupervisor.RetryNow"/>).
    /// </summary>
    DirectThenHcConnect,
    /// <summary>Only the core's socket or pipe.</summary>
    Direct,
    /// <summary>Only <c>hc connect</c>.</summary>
    HcConnect,
}

/// <summary>Settings of a <see cref="ConnectionSupervisor"/> (and of <c>AddHerdr</c>).</summary>
public sealed class HerdrOptions
{
    /// <summary>The hc session; null: <c>HC_SESSION</c>, else <c>default</c>.</summary>
    public string? Session { get; set; }

    /// <summary>The hc executable; null: found on PATH or in the installer's folder.</summary>
    public string? HcPath { get; set; }

    /// <summary>How to reach the core.</summary>
    public HerdrTransportPreference Transport { get; set; } = HerdrTransportPreference.DirectThenHcConnect;

    /// <summary>
    /// A command that carries the protocol on its stdin and stdout, used instead of this machine's core:
    /// program first, then its arguments, such as <c>ssh box hc connect --session work</c> (put the
    /// session in the command; <see cref="Session"/> and <see cref="Transport"/> do not apply). Like
    /// <c>hc connect</c> it may start a core, so after a loss the supervisor waits in
    /// <see cref="HerdrConnectionState.Disconnected"/> for <c>RetryNow(startCore: true)</c>.
    /// </summary>
    public IReadOnlyList<string>? Command { get; set; }

    /// <summary>
    /// Environment variables for <see cref="Command"/>'s process only, on top of this process's own; a
    /// null value removes one. For what a command reads from its environment, such as <c>SSH_ASKPASS</c>
    /// so ssh asks its questions through the caller. Never logged: values can be secrets.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? CommandEnvironment { get; set; }

    /// <summary>
    /// A transport the caller opens itself, used instead of this machine's core: an SSH channel to
    /// <c>hc connect</c> on another machine, from an app that cannot start <c>ssh</c>. It opens a fresh
    /// stream on every attempt. Treated like <see cref="Command"/>: it may start a core, so after a loss
    /// the supervisor waits in <see cref="HerdrConnectionState.Disconnected"/>. Wins over <see cref="Command"/>.
    /// </summary>
    public IHerdrTransport? Carrier { get; set; }

    /// <summary>Handshake settings, including the methods the caller needs.</summary>
    public HerdrConnectionOptions Connection { get; set; } = new();

    /// <summary>Waits between failed attempts; the last repeats. Default 0.5, 1, 2, 5 s.</summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; set; } =
        [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    /// <summary>
    /// How many attempts before the first connection, after which the supervisor stops trying and waits
    /// for <see cref="ConnectionSupervisor.RetryNow"/> (null: keep trying, with <see cref="ReconnectDelays"/>).
    /// For a place that may well be away (another computer over <see cref="Command"/>): each attempt runs
    /// the command and may start a core there. The state stays the last failure's, with no next attempt.
    /// </summary>
    public int? FirstAttempts { get; set; }

    /// <summary>Start a <see cref="TerminalMirror"/> on every connection (default true).</summary>
    public bool MirrorTerminals { get; set; } = true;

    /// <summary>Mirror only terminals carrying all these labels; null mirrors all (and document changes).</summary>
    public IReadOnlyDictionary<string, string>? MirrorLabels { get; set; }
}

/// <summary>Where a <see cref="ConnectionSupervisor"/> is.</summary>
public enum HerdrConnectionState
{
    /// <summary>Not started, or stopped.</summary>
    Stopped,
    /// <summary>Trying to connect (first time, or again after failures before ever connecting).</summary>
    Connecting,
    /// <summary>Connected; <see cref="ConnectionSupervisor.Current"/> is set.</summary>
    Connected,
    /// <summary>The connection was lost; trying again with backoff.</summary>
    Reconnecting,
    /// <summary>No core listens and no hc was found to reach one; still retrying at the longest delay.</summary>
    NotInstalled,
    /// <summary>The core speaks another generation or lacks a required method; waits for <see cref="ConnectionSupervisor.RetryNow"/>.</summary>
    Incompatible,
    /// <summary>
    /// The core went away and nothing listens any more: it was stopped. The supervisor keeps trying the
    /// endpoint (someone may start it) but never starts a core itself; <c>RetryNow(startCore: true)</c> does.
    /// </summary>
    CoreStopped,
    /// <summary>
    /// A connection that only <c>hc connect</c> or a <see cref="HerdrOptions.Command"/> makes was lost.
    /// Nothing tells a stopped core from a dropped line there without running that command, which
    /// starts a stopped core, so it waits for <c>RetryNow(startCore: true)</c>: one attempt per request.
    /// </summary>
    Disconnected,
}

/// <summary>A state change of a <see cref="ConnectionSupervisor"/>.</summary>
/// <param name="State">The new state.</param>
/// <param name="Error">Why the last attempt failed or the connection ended, when it did.</param>
/// <param name="Attempt">Failed attempts since the last successful connection.</param>
/// <param name="NextAttemptIn">When the next attempt starts, while waiting for one.</param>
public sealed record HerdrConnectionStateChange(HerdrConnectionState State, Exception? Error, int Attempt, TimeSpan? NextAttemptIn);

/// <summary>
/// Keeps one connection to the core alive: connects, watches it, reconnects with backoff after a loss,
/// falls back from the direct endpoint to <c>hc connect</c>, and starts a fresh <see cref="TerminalMirror"/>
/// on every connection. Streams do not survive a reconnect: attach again (with <c>resumed</c>) on
/// <see cref="HerdrConnectionState.Connected"/>. It never stops, restarts or updates a core.
/// </summary>
public sealed class ConnectionSupervisor : IAsyncDisposable
{
    readonly HerdrOptions _options;
    readonly Func<HerdrTransportPreference, IHerdrTransport?> _transports;
    readonly ILoggerFactory _loggers;
    readonly ILogger _logger;
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _wake = new(0, 1);
    readonly object _gate = new();
    TaskCompletionSource<HerdrConnection> _connected = NewConnectedSource();
    HerdrConnectionStateChange _state = new(HerdrConnectionState.Stopped, null, 0, null);
    Task _loop = Task.CompletedTask;
    int _started;
    int _disposed;
    int _startRequested;

    /// <summary>A supervisor for the core <paramref name="options"/> describe. Call <see cref="Start"/>.</summary>
    public ConnectionSupervisor(HerdrOptions options, ILoggerFactory? loggerFactory = null)
        : this(options, null, loggerFactory) { }

    internal ConnectionSupervisor(HerdrOptions options, Func<HerdrTransportPreference, IHerdrTransport?>? transports, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<ConnectionSupervisor>();
        _transports = transports ?? DefaultTransport;
    }

    /// <summary>The current state.</summary>
    public HerdrConnectionState State => Volatile.Read(ref _state).State;

    /// <summary>The current state with its error and timing.</summary>
    public HerdrConnectionStateChange StateDetails => Volatile.Read(ref _state);

    /// <summary>The live connection while <see cref="HerdrConnectionState.Connected"/>, else null.</summary>
    public HerdrConnection? Current { get; private set; }

    /// <summary>The live connection's mirror (when <see cref="HerdrOptions.MirrorTerminals"/>), else null.</summary>
    public TerminalMirror? Mirror { get; private set; }

    /// <summary>
    /// Raised on the supervisor's task for every state change. On <see cref="HerdrConnectionState.Connected"/>,
    /// <see cref="Current"/> and <see cref="Mirror"/> are the new ones and the mirror has its snapshot.
    /// </summary>
    public event Action<HerdrConnectionStateChange>? StateChanged;

    /// <summary>Starts connecting in the background. Once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>
    /// Skips the current wait and tries again now (also leaves <see cref="HerdrConnectionState.Incompatible"/>).
    /// With <paramref name="startCore"/>, that attempt may start a stopped core through <c>hc connect</c>:
    /// pass it only on the person's explicit request.
    /// </summary>
    public void RetryNow(bool startCore = false)
    {
        if (startCore)
            Volatile.Write(ref _startRequested, 1);
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Leaves the live connection and connects again now, through the transport as it is now: for when
    /// where to connect changed (a phone switching to another paired computer sets a new
    /// <see cref="HerdrOptions.Carrier"/>). Leaving sends CLOSE on the streams; the core is untouched.
    /// <paramref name="startCore"/> is as for <see cref="RetryNow"/>, and a connection only a carrier or a
    /// command makes needs it to come back (it waits for the person after a loss). With no live
    /// connection this is <see cref="RetryNow"/>.
    /// </summary>
    public void Reconnect(bool startCore = false)
    {
        HerdrConnection? current;
        lock (_gate)
            current = Current;
        if (current is null)
        {
            RetryNow(startCore);
            return;
        }

        if (startCore)
            Volatile.Write(ref _startRequested, 1);
        _logger.LogInformation("Leaving the connection to connect again");
        // The hold sees the connection end and the loop goes round: straight away when the start was
        // requested; otherwise after its wait, which the wake cuts short.
        _ = current.DisposeAsync().AsTask();
        if (!OnlyReachedByStarting)
            RetryNow();
    }

    /// <summary>The live connection, waiting for one when there is none.</summary>
    public Task<HerdrConnection> WaitForConnectionAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return _connected.Task.WaitAsync(cancellationToken);
    }

    async Task RunAsync(CancellationToken stop)
    {
        var failures = 0;
        var everConnected = false;
        HerdrConnectionState? steady = null;
        Exception? lastError = null;
        while (!stop.IsCancellationRequested)
        {
            Exception? error;
            HerdrConnectionState waitState;
            // hc connect starts a stopped core: allowed when the app starts, or when asked to, never after a loss.
            var mayStart = !everConnected || Interlocked.Exchange(ref _startRequested, 0) == 1;
            if (!mayStart && OnlyReachedByStarting)
            {
                Set(HerdrConnectionState.Disconnected, lastError, failures, null);
                if (!await WaitAsync(Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false))
                    break;
                continue;
            }

            // A stopped or missing core stays reported as such while the attempts go on: flipping to
            // Reconnecting for each attempt would tell the person something else every few seconds.
            if (steady is null)
                Set(everConnected ? HerdrConnectionState.Reconnecting : HerdrConnectionState.Connecting, null, failures, null);
            try
            {
                var (connection, mirror) = await ConnectOnceAsync(mayStart, stop).ConfigureAwait(false);
                failures = 0;
                everConnected = true;
                steady = null;
                error = await HoldAsync(connection, mirror, stop).ConfigureAwait(false);
                if (stop.IsCancellationRequested)
                    break;
                _logger.LogWarning("Lost the connection to herdr-core: {Message}", error?.Message ?? "closed");
                waitState = HerdrConnectionState.Reconnecting;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (HerdrIncompatibleException e)
            {
                failures++;
                Set(HerdrConnectionState.Incompatible, e, failures, null);
                if (!await WaitAsync(Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false))
                    break;
                continue;
            }
            catch (HcNotFoundException e)
            {
                error = e;
                waitState = HerdrConnectionState.NotInstalled;
            }
            catch (Exception e) when (e is HerdrException or IOException or UnauthorizedAccessException or TimeoutException)
            {
                error = e;
                waitState = !everConnected ? HerdrConnectionState.Connecting
                    : e is HerdrCoreUnavailableException && _options.Transport == HerdrTransportPreference.DirectThenHcConnect ? HerdrConnectionState.CoreStopped
                    : HerdrConnectionState.Reconnecting;
            }
            catch (Exception e)
            {
                // Anything else from an attempt (a caller's Carrier throwing its own library's exception,
                // a cancellation that was not ours): a failed attempt like the others. Letting it out
                // would end this loop and leave the state as it was, with nothing ever tried again.
                _logger.LogWarning("A connection attempt failed with {Kind}: {Message}", e.GetType().Name, e.Message);
                error = new HerdrConnectionException(e.Message, e);
                waitState = everConnected ? HerdrConnectionState.Reconnecting : HerdrConnectionState.Connecting;
            }

            lastError = error;
            if (everConnected && OnlyReachedByStarting)
            {
                failures++;
                continue; // straight to Disconnected: no attempt without the person
            }

            // Never reached, and the attempts allowed before that are spent: wait for the person.
            if (!everConnected && _options.FirstAttempts is { } allowed && failures + 1 >= allowed)
            {
                failures++;
                steady = null;
                Set(waitState, error, failures, null);
                if (!await WaitAsync(Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false))
                    break;
                continue;
            }

            steady = waitState is HerdrConnectionState.CoreStopped or HerdrConnectionState.NotInstalled ? waitState : null;
            var delay = Delay(waitState == HerdrConnectionState.NotInstalled ? int.MaxValue : failures);
            failures++;
            Set(waitState, error, failures, delay);
            if (!await WaitAsync(delay, stop).ConfigureAwait(false))
                break;
        }

        Set(HerdrConnectionState.Stopped, null, 0, null);
    }

    async Task<(HerdrConnection, TerminalMirror?)> ConnectOnceAsync(bool mayStart, CancellationToken stop)
    {
        var connection = await OpenAsync(mayStart, stop).ConfigureAwait(false);
        TerminalMirror? mirror = null;
        try
        {
            if (_options.MirrorTerminals)
            {
                mirror = new TerminalMirror(connection, _options.MirrorLabels, _loggers.CreateLogger<TerminalMirror>());
                await mirror.StartAsync(stop).ConfigureAwait(false);
            }
        }
        catch
        {
            if (mirror is not null)
                await mirror.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return (connection, mirror);
    }

    /// <summary>Every way in may start a stopped core: <c>hc connect</c> only, or a command.</summary>
    bool OnlyReachedByStarting => _options.Carrier is not null || _options.Command is not null || _options.Transport == HerdrTransportPreference.HcConnect;

    async Task<HerdrConnection> OpenAsync(bool mayStart, CancellationToken stop)
    {
        var logger = _loggers.CreateLogger<HerdrConnection>();
        if (OnlyReachedByStarting)
            return await HerdrConnection.ConnectAsync(HcConnect(), _options.Connection, logger, stop).ConfigureAwait(false);

        var direct = _transports(HerdrTransportPreference.Direct) ?? throw new InvalidOperationException("No direct transport.");
        try
        {
            return await HerdrConnection.ConnectAsync(direct, _options.Connection, logger, stop).ConfigureAwait(false);
        }
        catch (HerdrCoreUnavailableException e) when (_options.Transport == HerdrTransportPreference.DirectThenHcConnect && mayStart)
        {
            _logger.LogInformation("Nothing listens at {Endpoint}; trying hc connect", e.Endpoint);
            return await HerdrConnection.ConnectAsync(HcConnect(e), _options.Connection, logger, stop).ConfigureAwait(false);
        }
    }

    IHerdrTransport HcConnect(Exception? directFailure = null) =>
        _transports(HerdrTransportPreference.HcConnect)
        ?? throw new HcNotFoundException(_options.HcPath, directFailure);

    IHerdrTransport? DefaultTransport(HerdrTransportPreference kind) => kind switch
    {
        _ when _options.Carrier is { } carrier => carrier,
        _ when _options.Command is { Count: > 0 } command =>
            new HcConnectTransport(command[0], command.Skip(1).ToArray(), session: null, _loggers.CreateLogger<HcConnectTransport>(), _options.CommandEnvironment),
        HerdrTransportPreference.Direct => new EndpointTransport(HerdrEndpoint.Resolve(_options.Session)),
        _ => HcLocator.Find(_options.HcPath) is { } hc
            ? new HcConnectTransport(hc, _options.Session, _loggers.CreateLogger<HcConnectTransport>())
            : null,
    };

    /// <summary>Publishes the connection and waits until it ends; returns why.</summary>
    async Task<Exception?> HoldAsync(HerdrConnection connection, TerminalMirror? mirror, CancellationToken stop)
    {
        lock (_gate)
        {
            Current = connection;
            Mirror = mirror;
            _connected.TrySetResult(connection);
        }

        _wake.Wait(0); // a RetryNow pressed before this connection must not cut the next wait short
        Set(HerdrConnectionState.Connected, null, 0, null);

        var ended = mirror is null ? connection.Completion : Task.WhenAny(connection.Completion, mirror.Completion).Unwrap();
        Exception? error = null;
        try
        {
            await ended.WaitAsync(stop).ConfigureAwait(false);
            // The mirror's subscription ended while the connection lives: treat it as a loss.
            if (connection.IsConnected)
                error = new HerdrConnectionException("The event subscription ended.");
            // On a loss the connection ends its channels (so the mirror) before it faults its own
            // Completion: wait for that briefly to report why.
            else
                await connection.Completion.WaitAsync(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            error = e;
        }
        finally
        {
            lock (_gate)
            {
                Current = null;
                Mirror = null;
                if (_connected.Task.IsCompleted)
                    _connected = NewConnectedSource();
            }

            if (mirror is not null)
                await mirror.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false); // CLOSE on streams; the core is untouched
        }

        return error;
    }

    TimeSpan Delay(int failures)
    {
        var delays = _options.ReconnectDelays;
        if (delays.Count == 0)
            return TimeSpan.FromSeconds(1);
        return delays[Math.Clamp(failures, 0, delays.Count - 1)];
    }

    /// <summary>Waits for the delay or <see cref="RetryNow"/>; false when stopping.</summary>
    async Task<bool> WaitAsync(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await _wake.WaitAsync(delay, stop).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    void Set(HerdrConnectionState state, Exception? error, int attempt, TimeSpan? next)
    {
        var change = new HerdrConnectionStateChange(state, error, attempt, next);
        Volatile.Write(ref _state, change);
        try
        {
            StateChanged?.Invoke(change);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "A StateChanged handler threw on {State}", state);
        }
    }

    static TaskCompletionSource<HerdrConnection> NewConnectedSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Stops: disposes the mirror and the connection (CLOSE on every stream). Every program keeps running.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Attempts no longer end the loop; this is a defect (a state handler, the wait), so say it loudly.
            _logger.LogError(e, "The supervisor loop ended with an error");
        }

        lock (_gate)
            _connected.TrySetException(new ObjectDisposedException(nameof(ConnectionSupervisor)));
        _stop.Dispose();
        _wake.Dispose();
    }
}
