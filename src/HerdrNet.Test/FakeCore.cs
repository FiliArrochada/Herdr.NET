using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using HerdrNet.Framing;
using HerdrNet.Transport;

namespace HerdrNet.Test;

/// <summary>
/// A scripted herdr-core on the far end of an in-memory duplex pipe: the test decides every byte it
/// sends and reads every frame the client writes.
/// </summary>
internal sealed class FakeCore : IAsyncDisposable
{
    public const string DefaultHello =
        """{"generation":1,"version":"0.1.18","core":{"id":"c0ffee","name":"fake"},"framed":true,"methods":["hello","terminal.list","terminal.attach","events.subscribe"],"features":["automatic_input","resize_notifications"]}""";

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    readonly Pipe _toCore = new();
    readonly Pipe _toClient = new();

    public FakeCore() =>
        ClientStream = new DuplexStream(_toClient.Reader.AsStream(), _toCore.Writer.AsStream());

    /// <summary>The client's end.</summary>
    public Stream ClientStream { get; }

    public IHerdrTransport Transport => new StreamTransport(ClientStream, "fake core");

    /// <summary>Connects a client and answers its hello with <paramref name="hello"/>.</summary>
    public static async Task<(HerdrConnection Connection, FakeCore Core)> ConnectedAsync(string hello = DefaultHello)
    {
        var core = new FakeCore();
        var connecting = HerdrConnection.ConnectAsync(core.Transport);
        var request = await core.ReadLineAsync();
        await core.SendLineAsync($$"""{"jsonrpc":"2.0","id":{{request.GetProperty("id").GetInt64()}},"result":{{hello}}}""");
        var connection = await connecting.WaitAsync(Timeout);
        return (connection, core);
    }

    // ---- plain mode ----

    public async Task<JsonElement> ReadLineAsync()
    {
        var reader = _toCore.Reader;
        while (true)
        {
            var read = await reader.ReadAsync().AsTask().WaitAsync(Timeout);
            var buffer = read.Buffer;
            if (FrameCodec.TryReadLine(ref buffer, out var line))
            {
                var element = JsonDocument.Parse(line).RootElement.Clone();
                reader.AdvanceTo(buffer.Start);
                return element;
            }

            if (read.IsCompleted)
                throw new EndOfStreamException("The client closed before sending a line.");
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public Task SendLineAsync(string json) => SendRawAsync(Encoding.UTF8.GetBytes(json + "\n"));

    // ---- framed mode ----

    public async Task SendRawAsync(byte[] bytes)
    {
        await _toClient.Writer.WriteAsync(bytes);
    }

    public Task SendFrameAsync(uint channel, FrameKind kind, ReadOnlySpan<byte> payload) =>
        SendRawAsync(FrameCodec.Encode(channel, kind, payload));

    public Task SendDataAsync(uint channel, string text) => SendFrameAsync(channel, FrameKind.Data, Encoding.UTF8.GetBytes(text));

    public Task ReplyAsync(long id, string resultJson) =>
        SendDataAsync(0, $$"""{"jsonrpc":"2.0","id":{{id}},"result":{{resultJson}}}""");

    public Task ReplyErrorAsync(long id, string kind, string message, string hint) =>
        SendDataAsync(0, $$"""{"jsonrpc":"2.0","id":{{id}},"error":{"code":-32000,"message":"{{message}}","data":{"kind":"{{kind}}","hint":"{{hint}}" """ + "}}}");

    /// <summary>The next frame the client sent.</summary>
    public async Task<(FrameHeader Header, byte[] Payload)> ReadFrameAsync()
    {
        var reader = _toCore.Reader;
        while (true)
        {
            var read = await reader.ReadAsync().AsTask().WaitAsync(Timeout);
            var buffer = read.Buffer;
            if (FrameCodec.TryReadFrame(ref buffer, out var header, out var payload))
            {
                var bytes = payload.ToArray();
                reader.AdvanceTo(buffer.Start);
                return (header, bytes);
            }

            if (read.IsCompleted)
                throw new EndOfStreamException("The client closed the connection.");
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>The next JSON-RPC request on channel 0, skipping other frames into <see cref="Skipped"/>.</summary>
    public async Task<JsonElement> ReadRequestAsync()
    {
        while (true)
        {
            var (header, payload) = await ReadFrameAsync();
            if (header.Channel == 0 && header.Kind == FrameKind.Data)
                return JsonDocument.Parse(payload).RootElement.Clone();
            Skipped.Add((header, payload));
        }
    }

    /// <summary>The next frame of <paramref name="kind"/> on <paramref name="channel"/>, skipping others.</summary>
    public async Task<byte[]> ReadFrameAsync(uint channel, FrameKind kind)
    {
        while (true)
        {
            var (header, payload) = await ReadFrameAsync();
            if (header.Channel == channel && header.Kind == kind)
                return payload;
            Skipped.Add((header, payload));
        }
    }

    public List<(FrameHeader Header, byte[] Payload)> Skipped { get; } = [];

    /// <summary>Hangs up on the client.</summary>
    public async Task DisconnectAsync() => await _toClient.Writer.CompleteAsync();

    public static uint ReadCredit(byte[] payload) => BinaryPrimitives.ReadUInt32LittleEndian(payload);

    public async ValueTask DisposeAsync()
    {
        await _toClient.Writer.CompleteAsync();
        await _toCore.Reader.CompleteAsync();
    }

    sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => read.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => write.WriteAsync(buffer, cancellationToken);
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
