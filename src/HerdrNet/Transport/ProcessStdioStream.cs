using System.Diagnostics;

namespace HerdrNet.Transport;

/// <summary>A child process's stdout (read) and stdin (write) as one duplex stream.</summary>
internal sealed class ProcessStdioStream(Process process) : Stream
{
    readonly Stream _read = process.StandardOutput.BaseStream;
    readonly Stream _write = process.StandardInput.BaseStream;
    readonly Queue<string> _stderr = new();
    int _disposed;

    /// <summary>The last lines the process wrote to stderr, for error messages.</summary>
    public string StderrTail
    {
        get { lock (_stderr) return string.Join(Environment.NewLine, _stderr); }
    }

    internal void RecordStderr(string line)
    {
        lock (_stderr)
        {
            _stderr.Enqueue(line);
            while (_stderr.Count > 20)
                _stderr.Dequeue();
        }
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _read.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _write.WriteAsync(buffer, cancellationToken);
    public override void Flush() => _write.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // EOF on stdin lets the relay end on its own; the core and its terminals are unaffected.
        try { await _write.DisposeAsync().ConfigureAwait(false); } catch (IOException) { }
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
        }

        await _read.DisposeAsync().ConfigureAwait(false);
        process.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
}
