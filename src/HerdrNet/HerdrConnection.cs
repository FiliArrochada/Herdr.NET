using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HerdrNet.Framing;
using HerdrNet.Model;
using HerdrNet.Serialization;
using HerdrNet.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HerdrNet;

/// <summary>Settings of a <see cref="HerdrConnection"/>.</summary>
public sealed record HerdrConnectionOptions
{
    /// <summary>The protocol generation this client speaks.</summary>
    public const uint ProtocolGeneration = 1;

    /// <summary>How long the <c>hello</c> handshake may take.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Methods the caller needs; connecting fails with <see cref="HerdrIncompatibleException"/> when the core lacks one.</summary>
    public IReadOnlyList<string> RequiredMethods { get; init; } = [];
}

/// <summary>
/// One framed connection to a herdr-core: concurrent JSON-RPC calls on channel 0, plus terminal
/// and event streams on their own channels, each with the core's credit-based flow control.
/// Closing it ends no program: the core's holders own them.
/// </summary>
public sealed class HerdrConnection : IAsyncDisposable
{
    const uint ControlChannel = 0;
    const int MaxPlainLine = FrameCodec.MaxPayload;

    readonly Stream _stream;
    readonly PipeReader _input;
    readonly ILogger _logger;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly ConcurrentDictionary<long, PendingCall> _pending = new();
    readonly ConcurrentDictionary<uint, ChannelEndpoint> _channels = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long _nextId;
    int _ended;
    int _disposed;
    Task _readLoop = Task.CompletedTask;

    HerdrConnection(Stream stream, string description, ILogger logger)
    {
        _stream = stream;
        Description = description;
        _logger = logger;
        _input = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 64 * 1024, leaveOpen: true));
        Terminals = new TerminalsClient(this);
        Events = new EventsClient(this);
        Documents = new DocumentsClient(this);
        Server = new ServerClient(this);
        Templates = new TemplatesClient(this);
        Machines = new MachinesClient(this);
    }

    /// <summary>Where this connection goes.</summary>
    public string Description { get; }

    /// <summary>The core's handshake reply: generation, version, identity, methods, features.</summary>
    public HelloResult Hello { get; private set; } = new();

    /// <summary>Completes when the connection ends: normally after <see cref="DisposeAsync"/>, faulted with a <see cref="HerdrConnectionException"/> when lost.</summary>
    public Task Completion => _completion.Task;

    /// <summary>False once the connection has ended.</summary>
    public bool IsConnected => Volatile.Read(ref _ended) == 0;

    /// <summary>Terminal methods and attach streams.</summary>
    public TerminalsClient Terminals { get; }

    /// <summary>Event subscriptions.</summary>
    public EventsClient Events { get; }

    /// <summary>Versioned JSON documents kept by the core.</summary>
    public DocumentsClient Documents { get; }

    /// <summary>The core's own status.</summary>
    public ServerClient Server { get; }

    /// <summary>Named agent roles.</summary>
    public TemplatesClient Templates { get; }

    /// <summary>Other cores this core reaches.</summary>
    public MachinesClient Machines { get; }

    /// <summary>Whether the core advertised <paramref name="feature"/> in its handshake.</summary>
    public bool Supports(string feature) => Hello.Features.Contains(feature);

    /// <summary>Connects to the core of <paramref name="session"/> on this machine through its socket or pipe.</summary>
    public static Task<HerdrConnection> ConnectAsync(string? session = null, HerdrConnectionOptions? options = null, ILogger? logger = null, CancellationToken cancellationToken = default) =>
        ConnectAsync(new EndpointTransport(HerdrEndpoint.Resolve(session)), options, logger, cancellationToken);

    /// <summary>Opens <paramref name="transport"/>, says <c>hello</c> and switches to framed mode.</summary>
    public static async Task<HerdrConnection> ConnectAsync(IHerdrTransport transport, HerdrConnectionOptions? options = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        options ??= new HerdrConnectionOptions();
        var stream = await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        var connection = new HerdrConnection(stream, transport.Description, logger ?? NullLogger.Instance);
        try
        {
            await connection.HandshakeAsync(options, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception e)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            if (e is HerdrConnectionException && stream is ProcessStdioStream { StderrTail: { Length: > 0 } tail })
                throw new HerdrConnectionException($"{e.Message} {transport.Description} said: {tail}", e);
            throw;
        }
    }

    // ---------------- handshake (plain mode) ----------------

    async Task HandshakeAsync(HerdrConnectionOptions options, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HandshakeTimeout);

        var id = Interlocked.Increment(ref _nextId);
        var request = EncodeRequest(id, "hello", new HelloParams(HerdrConnectionOptions.ProtocolGeneration, Framed: true), HerdrJsonContext.Default.HelloParams);
        var line = new byte[request.Length + 1];
        request.CopyTo(line);
        line[^1] = (byte)'\n';
        try
        {
            await _stream.WriteAsync(line, timeout.Token).ConfigureAwait(false);
            await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            var result = await ReadPlainReplyAsync(id, timeout.Token).ConfigureAwait(false);
            Hello = result.Deserialize(HerdrJsonContext.Default.HelloResult) ?? throw new HerdrProtocolException("Empty hello reply.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HerdrConnectionException($"{Description} did not answer hello within {options.HandshakeTimeout.TotalSeconds:0.#} s.");
        }
        catch (IOException e)
        {
            throw new HerdrConnectionException($"{Description} closed during hello: {e.Message}", e);
        }
        catch (HerdrRpcException e) when (e.Kind == HerdrErrorKinds.UnsupportedGeneration)
        {
            throw new HerdrIncompatibleException($"The core does not speak protocol generation {HerdrConnectionOptions.ProtocolGeneration}: {e.Message}");
        }

        if (Hello.Generation != HerdrConnectionOptions.ProtocolGeneration)
            throw new HerdrIncompatibleException($"The core speaks protocol generation {Hello.Generation}; this client speaks {HerdrConnectionOptions.ProtocolGeneration}.");
        if (!Hello.Framed)
            throw new HerdrIncompatibleException("The core did not switch to framed mode.");
        var missing = options.RequiredMethods.Where(m => !Hello.Methods.Contains(m)).ToArray();
        if (missing.Length > 0)
            throw new HerdrIncompatibleException($"Core {Hello.Version} lacks {string.Join(", ", missing)}; update it with `hc update`.");

        _logger.LogInformation("Connected to herdr-core {Version} ({Core}) at {Endpoint}", Hello.Version, Hello.Core.Name, Description);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    async Task<JsonElement> ReadPlainReplyAsync(long id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var read = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = read.Buffer;
            while (FrameCodec.TryReadLine(ref buffer, out var line))
            {
                if (line.IsEmpty)
                    continue;
                using var message = ParseJson(line);
                var root = message.RootElement;
                if (!root.TryGetProperty("id", out var replyId) || replyId.ValueKind != JsonValueKind.Number || replyId.GetInt64() != id)
                    continue; // nothing else is expected before the hello reply; ignore it
                // Everything after this line is framed: leave it in the pipe for the reader loop.
                _input.AdvanceTo(buffer.Start);
                return TakeResult(root, "hello");
            }

            if (read.IsCompleted)
                throw new HerdrConnectionException($"{Description} closed before answering hello.");
            if (buffer.Length > MaxPlainLine)
                throw new HerdrProtocolException("The hello reply is over 16 MiB without a newline.");
            _input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    // ---------------- calls ----------------

    /// <summary>Calls any method with raw JSON parameters, for methods this client does not model yet.</summary>
    public async Task<JsonElement> RawCallAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        var (result, _) = await CallCoreAsync(method, writer =>
        {
            if (parameters is { } p)
            {
                p.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
        }, opensChannel: false, cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal async Task<TResult> CallAsync<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        var (result, _) = await CallCoreAsync(method, w => JsonSerializer.Serialize(w, parameters, paramsInfo), opensChannel: false, cancellationToken).ConfigureAwait(false);
        return Deserialize(method, result, resultInfo);
    }

    internal async Task<(TResult Result, ChannelEndpoint Channel)> OpenChannelAsync<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        var (result, channel) = await CallCoreAsync(method, w => JsonSerializer.Serialize(w, parameters, paramsInfo), opensChannel: true, cancellationToken).ConfigureAwait(false);
        if (channel is null)
            throw new HerdrProtocolException($"{method} answered without a channel.");
        return (Deserialize(method, result, resultInfo), channel);
    }

    static TResult Deserialize<TResult>(string method, JsonElement result, JsonTypeInfo<TResult> info)
    {
        try
        {
            return result.Deserialize(info) ?? throw new HerdrProtocolException($"{method} answered null.");
        }
        catch (JsonException e)
        {
            throw new HerdrProtocolException($"{method} answered a shape this client does not understand: {e.Message}", e);
        }
    }

    async Task<(JsonElement Result, ChannelEndpoint? Channel)> CallCoreAsync(string method, Action<Utf8JsonWriter> writeParams, bool opensChannel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected)
            throw new HerdrConnectionException($"The connection to {Description} has ended.");

        var id = Interlocked.Increment(ref _nextId);
        var call = new PendingCall(method, opensChannel);
        _pending[id] = call;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var request = EncodeRequest(id, method, writeParams);
            await SendFrameAsync(ControlChannel, FrameKind.Data, request, cancellationToken).ConfigureAwait(false);
            await using (cancellationToken.Register(() => call.Cancel(cancellationToken)).ConfigureAwait(false))
            {
                var outcome = await call.Task.ConfigureAwait(false);
                _logger.LogDebug("{Method} #{Id} answered in {Elapsed:0.0} ms", method, id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return outcome;
            }
        }
        finally
        {
            // A call that opens a channel stays registered after cancellation, so its late reply can close the channel.
            if (!opensChannel || !call.Task.IsCanceled)
                _pending.TryRemove(id, out _);
        }
    }

    static byte[] EncodeRequest<T>(long id, string method, T parameters, JsonTypeInfo<T> info) =>
        EncodeRequest(id, method, w => JsonSerializer.Serialize(w, parameters, info));

    static byte[] EncodeRequest(long id, string method, Action<Utf8JsonWriter> writeParams)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            writeParams(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    // ---------------- writing frames ----------------

    internal async ValueTask SendFrameAsync(uint channel, FrameKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > FrameCodec.MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(payload), "Frame payloads are at most 16 MiB.");
        var header = new byte[FrameCodec.HeaderSize];
        FrameCodec.WriteHeader(header, channel, kind, payload.Length);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsConnected)
                throw new HerdrConnectionException($"The connection to {Description} has ended.");
            await _stream.WriteAsync(header, CancellationToken.None).ConfigureAwait(false);
            if (!payload.IsEmpty)
                await _stream.WriteAsync(payload, CancellationToken.None).ConfigureAwait(false);
            await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            var lost = new HerdrConnectionException($"Lost the connection to {Description}: {e.Message}", e);
            End(lost);
            throw lost;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal void SendCreditInBackground(uint channel, uint bytes)
    {
        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, bytes);
        _ = SendQuietlyAsync(channel, FrameKind.Credit, payload);
    }

    async Task SendQuietlyAsync(uint channel, FrameKind kind, byte[] payload)
    {
        try
        {
            await SendFrameAsync(channel, kind, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (HerdrConnectionException)
        {
            // The connection's end is reported through Completion and the channels.
        }
    }

    // ---------------- reading frames ----------------

    async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                var read = await _input.ReadAsync(_shutdown.Token).ConfigureAwait(false);
                var buffer = read.Buffer;
                while (FrameCodec.TryReadFrame(ref buffer, out var header, out var payload))
                    await DispatchAsync(header, payload).ConfigureAwait(false);
                _input.AdvanceTo(buffer.Start, buffer.End);

                if (read.IsCompleted)
                {
                    error = new HerdrConnectionException(buffer.IsEmpty
                        ? $"{Description} closed the connection."
                        : $"{Description} closed the connection in the middle of a frame.");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            error = new HerdrConnectionException($"Lost the connection to {Description}: {e.Message}", e);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "The connection to {Endpoint} failed", Description);
            error = e as HerdrException ?? new HerdrProtocolException(e.Message, e);
        }

        End(error);
    }

    async ValueTask DispatchAsync(FrameHeader header, ReadOnlySequence<byte> payload)
    {
        if (header.Channel == ControlChannel)
        {
            if (header.Kind == FrameKind.Data)
                HandleControlMessage(payload);
            return;
        }

        if (!_channels.TryGetValue(header.Channel, out var channel))
        {
            _logger.LogTrace("Dropped {Kind} for unknown channel {Channel}", header.Kind, header.Channel);
            return;
        }

        switch (header.Kind)
        {
            case FrameKind.Data:
                // Copies the bytes before the read buffer is released; never waits on the consumer.
                await channel.OnDataAsync(payload).ConfigureAwait(false);
                break;
            case FrameKind.Close:
                channel.OnCoreClose(payload);
                break;
            case FrameKind.Resize:
                channel.OnResize(payload);
                break;
            default:
                _logger.LogTrace("Ignored {Kind} on channel {Channel}", header.Kind, header.Channel);
                break;
        }
    }

    void HandleControlMessage(in ReadOnlySequence<byte> payload)
    {
        using var message = ParseJson(payload);
        var root = message.RootElement;
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
        {
            if (root.TryGetProperty("method", out var method))
                _logger.LogDebug("Notification {Method} on channel 0 ignored", method.GetString());
            return;
        }

        var id = idElement.GetInt64();
        if (!_pending.TryRemove(id, out var call))
            return;

        JsonElement result;
        try
        {
            result = TakeResult(root, call.Method);
        }
        catch (HerdrRpcException e)
        {
            call.Fail(e);
            return;
        }

        // The core sends the reply before any frame of the channel it opens, and this loop reads in
        // order, so registering here (not in the awaiting caller) loses no first-paint bytes.
        ChannelEndpoint? channel = null;
        if (call.OpensChannel && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("channel", out var channelElement) && channelElement.ValueKind == JsonValueKind.Number)
        {
            channel = new ChannelEndpoint(this, channelElement.GetUInt32());
            _channels[channel.Id] = channel;
        }

        if (!call.Complete(result, channel) && channel is not null)
        {
            // Nobody waits for it any more (cancelled): close it again.
            _ = channel.CloseAsync(TimeSpan.FromSeconds(5)).AsTask();
        }
    }

    static JsonElement TakeResult(JsonElement root, string method)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
            var text = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            string? kind = null, hint = null;
            if (error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                kind = data.TryGetProperty("kind", out var k) ? k.GetString() : null;
                hint = data.TryGetProperty("hint", out var h) ? h.GetString() : null;
            }

            throw new HerdrRpcException(method, code, text, kind, hint);
        }

        return root.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    static JsonDocument ParseJson(in ReadOnlySequence<byte> bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException e)
        {
            throw new HerdrProtocolException($"The core sent malformed JSON: {e.Message}", e);
        }
    }

    internal void ForgetChannel(ChannelEndpoint channel) => _channels.TryRemove(new KeyValuePair<uint, ChannelEndpoint>(channel.Id, channel));

    // ---------------- ending ----------------

    void End(Exception? error)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;
        if (error is not null)
            _logger.LogWarning("Connection to {Endpoint} ended: {Message}", Description, error.Message);

        _shutdown.Cancel();
        var lost = error as HerdrConnectionException
            ?? new HerdrConnectionException(error is null ? $"The connection to {Description} was closed." : $"Lost the connection to {Description}: {error.Message}", error);
        foreach (var call in _pending.Values)
            call.Fail(lost);
        _pending.Clear();
        foreach (var channel in _channels.Values)
            channel.OnConnectionEnded(error);

        if (error is null)
            _completion.TrySetResult();
        else
            _completion.TrySetException(lost);
    }

    /// <summary>
    /// Closes the connection. Attached streams end as <c>connection_closed</c>; the core's terminals and
    /// their programs keep running.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        End(null);
        await _stream.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Reader loop ended with an error during dispose");
        }

        await _input.CompleteAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    sealed class PendingCall(string method, bool opensChannel)
    {
        readonly TaskCompletionSource<(JsonElement, ChannelEndpoint?)> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Method { get; } = method;
        public bool OpensChannel { get; } = opensChannel;
        public Task<(JsonElement Result, ChannelEndpoint? Channel)> Task => _tcs.Task;

        public bool Complete(JsonElement result, ChannelEndpoint? channel) => _tcs.TrySetResult((result, channel));
        public void Fail(Exception error) => _tcs.TrySetException(error);
        public void Cancel(CancellationToken token) => _tcs.TrySetCanceled(token);
    }
}
