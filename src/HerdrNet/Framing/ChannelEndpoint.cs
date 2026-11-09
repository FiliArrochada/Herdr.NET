using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;
using HerdrNet.Serialization;

namespace HerdrNet.Framing;

/// <summary>How a stream channel ended.</summary>
internal sealed record ChannelClosed(string Reason, int? ExitCode, int? Signal, Exception? Error);

/// <summary>
/// One stream channel of a framed connection: bytes in through a pipe the reader loop fills,
/// CREDIT out as the consumer advances, CLOSE either way.
/// </summary>
internal sealed class ChannelEndpoint
{
    // Core bounds what it sends by the credit we return, so the pipe never pauses the reader loop.
    static readonly PipeOptions Options = new(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false);

    // Return credit in batches, but always once the consumer has drained what it read.
    internal const int CreditBatch = 16 * 1024;

    readonly HerdrConnection _connection;
    readonly Pipe _pipe = new(Options);
    readonly TaskCompletionSource<ChannelClosed> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // RESIZE frames, each at the stream offset where it arrived; the consumer reaches them in order.
    readonly ConcurrentQueue<GridMark> _grids = new();
    long _written; // reader loop only
    long _unreturnedCredit;
    int _closeSent;

    public ChannelEndpoint(HerdrConnection connection, uint id)
    {
        _connection = connection;
        Id = id;
        Reader = new CreditingPipeReader(_pipe.Reader, this);
    }

    public uint Id { get; }

    /// <summary>The consumer's view: advancing it returns credit to the core.</summary>
    public PipeReader Reader { get; }

    public Task<ChannelClosed> Closed => _closed.Task;

    public bool IsClosed => _closed.Task.IsCompleted;

    /// <summary>
    /// Raised when the consumer reaches a grid the core announced (observers that opted in): on the
    /// consumer's thread, inside <see cref="Reader"/>'s read, before any byte that followed the RESIZE.
    /// </summary>
    public Action<ushort, ushort>? ResizeReceived { get; set; }

    // ---- called by the connection's reader loop, one frame at a time ----

    internal ValueTask<FlushResult> OnDataAsync(in ReadOnlySequence<byte> payload)
    {
        if (IsClosed)
            return default;
        foreach (var segment in payload)
            _pipe.Writer.Write(segment.Span);
        _written += payload.Length;
        return _pipe.Writer.FlushAsync();
    }

    internal void OnResize(in ReadOnlySequence<byte> payload)
    {
        if (payload.Length < 4)
            return;
        Span<byte> raw = stackalloc byte[4];
        payload.Slice(0, 4).CopyTo(raw);
        // Queued before any later DATA is flushed, so a consumer that sees those bytes sees this mark.
        _grids.Enqueue(new GridMark(_written, BinaryPrimitives.ReadUInt16LittleEndian(raw), BinaryPrimitives.ReadUInt16LittleEndian(raw[2..])));
    }

    /// <summary>
    /// Raises every grid at or before <paramref name="consumed"/> and returns how many bytes after it the
    /// consumer may see before the next grid applies (or <paramref name="available"/> when none does).
    /// </summary>
    internal long ApplyGrids(long consumed, long available)
    {
        while (_grids.TryPeek(out var mark))
        {
            if (mark.Offset > consumed)
                return Math.Min(available, mark.Offset - consumed);
            _grids.TryDequeue(out _);
            ResizeReceived?.Invoke(mark.Cols, mark.Rows);
        }

        return available;
    }

    internal void OnCoreClose(in ReadOnlySequence<byte> payload)
    {
        ChannelClosed closed;
        if (payload.IsEmpty)
        {
            closed = new ChannelClosed(TerminalCloseReasons.Detached, null, null, null);
        }
        else
        {
            try
            {
                var reader = new Utf8JsonReader(payload);
                var reason = JsonSerializer.Deserialize(ref reader, HerdrJsonContext.Default.CloseReason);
                closed = new ChannelClosed(reason?.Reason ?? TerminalCloseReasons.Unknown, reason?.ExitCode, reason?.Signal, null);
            }
            catch (JsonException)
            {
                closed = new ChannelClosed(TerminalCloseReasons.Unknown, null, null, null);
            }
        }

        Finish(closed);
    }

    internal void OnConnectionEnded(Exception? error) =>
        Finish(new ChannelClosed(error is null ? TerminalCloseReasons.ConnectionClosed : TerminalCloseReasons.ConnectionLost, null, null, error));

    void Finish(ChannelClosed closed)
    {
        if (_closed.TrySetResult(closed))
        {
            _pipe.Writer.Complete();
            _connection.ForgetChannel(this);
        }
    }

    // ---- called by the consumer ----

    /// <remarks>
    /// Never throws synchronously: on a closed channel the returned task is faulted, so a caller that
    /// fires and forgets (focus and resize from UI events, which can race a detach) cannot have the
    /// exception escape into its own call stack.
    /// </remarks>
    public ValueTask SendAsync(FrameKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (IsClosed)
            return ValueTask.FromException(new InvalidOperationException($"Channel {Id} is closed."));
        return _connection.SendFrameAsync(Id, kind, payload, cancellationToken);
    }

    /// <summary>Sends CLOSE once and waits for the core's answer (or the timeout, then closes locally).</summary>
    public async ValueTask CloseAsync(TimeSpan wait)
    {
        if (IsClosed)
            return;
        if (Interlocked.Exchange(ref _closeSent, 1) == 0)
        {
            try
            {
                await _connection.SendFrameAsync(Id, FrameKind.Close, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false);
            }
            catch (HerdrConnectionException)
            {
                // The connection ended meanwhile; its teardown closes this channel.
            }
        }

        try
        {
            await _closed.Task.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Finish(new ChannelClosed(TerminalCloseReasons.Detached, null, null, null));
        }
    }

    internal void OnConsumed(long bytes, bool drained)
    {
        if (bytes <= 0 || IsClosed)
            return;
        var pending = Interlocked.Add(ref _unreturnedCredit, bytes);
        if (pending < CreditBatch && !drained)
            return;
        pending = Interlocked.Exchange(ref _unreturnedCredit, 0);
        while (pending > 0)
        {
            var chunk = (uint)Math.Min(pending, uint.MaxValue);
            pending -= chunk;
            _connection.SendCreditInBackground(Id, chunk);
        }
    }
}

readonly record struct GridMark(long Offset, ushort Cols, ushort Rows);

/// <summary>
/// The consumer's pipe reader: reports how many bytes each <c>AdvanceTo</c> consumed (for credit), and
/// cuts each read short at the next announced grid so the grid is applied before the bytes after it.
/// </summary>
/// <remarks>
/// Disposing a stream or subscription completes this reader from the disposing thread while the
/// consumer may be mid-read (awaiting, or holding a buffer it has not advanced). Completing the pipe
/// then would free that buffer under it and make its <c>AdvanceTo</c> throw, so a completion that
/// arrives mid-read waits for the consumer's <c>AdvanceTo</c>; reads after it report the end.
/// </remarks>
internal sealed class CreditingPipeReader(PipeReader inner, ChannelEndpoint channel) : PipeReader
{
    static readonly ReadResult Ended = new(ReadOnlySequence<byte>.Empty, isCanceled: false, isCompleted: true);

    readonly Lock _gate = new();
    ReadOnlySequence<byte> _last;
    long _consumed;
    bool _inRead;          // from a read starting until its AdvanceTo
    bool _completeWanted;  // Complete was called
    bool _innerCompleted;
    Exception? _completeError;

    public override bool TryRead(out ReadResult result)
    {
        lock (_gate)
        {
            if (_completeWanted)
            {
                result = Ended;
                return true;
            }

            if (!inner.TryRead(out result))
                return false;
            _inRead = true;
            result = Shape(result);
            return true;
        }
    }

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_completeWanted)
                return Ended;
            _inRead = true;
        }

        ReadResult result;
        try
        {
            result = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            EndRead();
            throw;
        }

        lock (_gate)
            return Shape(result);
    }

    ReadResult Shape(ReadResult result)
    {
        var buffer = result.Buffer;
        var visible = channel.ApplyGrids(_consumed, buffer.Length);
        if (visible < buffer.Length)
            result = new ReadResult(buffer.Slice(0, visible), result.IsCanceled, isCompleted: false);
        _last = result.Buffer;
        return result;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        long bytes;
        bool drained;
        lock (_gate)
        {
            if (_innerCompleted)
                return; // advancing the empty result that reported the end
            bytes = _last.IsEmpty ? 0 : _last.Slice(_last.Start, consumed).Length;
            drained = !_last.IsEmpty && _last.End.Equals(consumed);
            _last = default;
            _consumed += bytes;
            inner.AdvanceTo(consumed, examined);
            _inRead = false;
            CompleteInnerIfWanted();
        }

        channel.OnConsumed(bytes, drained);
    }

    public override void CancelPendingRead() => inner.CancelPendingRead();

    public override void Complete(Exception? exception = null)
    {
        lock (_gate)
        {
            if (_completeWanted)
                return;
            _completeWanted = true;
            _completeError = exception;
            if (!_inRead)
                CompleteInnerIfWanted();
        }
    }

    void EndRead()
    {
        lock (_gate)
        {
            _inRead = false;
            CompleteInnerIfWanted();
        }
    }

    void CompleteInnerIfWanted()
    {
        if (!_completeWanted || _innerCompleted)
            return;
        _innerCompleted = true;
        _last = default;
        inner.Complete(_completeError);
    }
}
