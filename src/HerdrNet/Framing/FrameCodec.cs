using System.Buffers;
using System.Buffers.Binary;

namespace HerdrNet.Framing;

/// <summary>Frame kinds of herdr-core's framed mode (protocol generation 1).</summary>
internal enum FrameKind : byte
{
    /// <summary>Channel 0: one JSON-RPC message. Other channels: stream bytes.</summary>
    Data = 0,
    /// <summary>u32 LE: bytes the receiver consumed.</summary>
    Credit = 1,
    /// <summary>The channel is over; from the core, a JSON reason (or empty, answering ours).</summary>
    Close = 2,
    /// <summary>u16 LE cols, u16 LE rows.</summary>
    Resize = 3,
    /// <summary>One byte: 1 gained focus, 0 lost it.</summary>
    Focus = 4,
    /// <summary>JSON display facts.</summary>
    Facts = 5,
    /// <summary>Terminal-generated reply bytes, not human activity (only when negotiated).</summary>
    AutomaticInput = 6,
}

internal readonly record struct FrameHeader(uint Channel, FrameKind Kind, uint Length);

/// <summary>
/// <c>[channel u32 LE][kind u8][len u32 LE][payload]</c>, payload at most 16 MiB.
/// </summary>
internal static class FrameCodec
{
    public const int HeaderSize = 9;
    public const int MaxPayload = 16 * 1024 * 1024;

    /// <summary>
    /// Reads one whole frame from the front of <paramref name="buffer"/> and moves it past the frame.
    /// Returns false, leaving the buffer untouched, when the frame is not complete yet.
    /// The payload is a slice of the buffer: copy it before the buffer is released.
    /// </summary>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out FrameHeader header, out ReadOnlySequence<byte> payload)
    {
        header = default;
        payload = default;
        if (buffer.Length < HeaderSize)
            return false;

        Span<byte> raw = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(raw);
        var channel = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        var kind = (FrameKind)raw[4];
        var length = BinaryPrimitives.ReadUInt32LittleEndian(raw[5..]);
        if (length > MaxPayload)
            throw new HerdrProtocolException($"Frame on channel {channel} declares {length} bytes, over the 16 MiB limit.");

        if (buffer.Length < HeaderSize + length)
            return false;

        header = new FrameHeader(channel, kind, length);
        payload = buffer.Slice(HeaderSize, length);
        buffer = buffer.Slice(HeaderSize + length);
        return true;
    }

    public static void WriteHeader(Span<byte> destination, uint channel, FrameKind kind, int length)
    {
        if ((uint)length > MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Frame payloads are at most 16 MiB.");
        BinaryPrimitives.WriteUInt32LittleEndian(destination, channel);
        destination[4] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[5..], (uint)length);
    }

    /// <summary>Writes a whole frame (header and payload) into a new array.</summary>
    public static byte[] Encode(uint channel, FrameKind kind, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        WriteHeader(frame, channel, kind, payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>Takes one newline-terminated line off the front of <paramref name="buffer"/>, without the newline.</summary>
    public static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var newline = buffer.PositionOf((byte)'\n');
        if (newline is null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, newline.Value);
        if (!line.IsEmpty && EndsWithCarriageReturn(line))
            line = line.Slice(0, line.Length - 1);
        buffer = buffer.Slice(buffer.GetPosition(1, newline.Value));
        return true;
    }

    static bool EndsWithCarriageReturn(in ReadOnlySequence<byte> line)
    {
        var last = line.Slice(line.Length - 1);
        return last.FirstSpan[0] == (byte)'\r';
    }
}
