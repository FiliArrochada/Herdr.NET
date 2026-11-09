using System.Buffers;
using System.Text;
using HerdrNet.Framing;

namespace HerdrNet.Test;

public class FrameCodecTests
{
    [Fact]
    public void Encodes_header_little_endian()
    {
        var frame = FrameCodec.Encode(0x01020304, FrameKind.Resize, [0xAA, 0xBB]);

        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01, 3, 2, 0, 0, 0, 0xAA, 0xBB }, frame);
    }

    [Fact]
    public void Reads_several_frames_from_one_buffer()
    {
        var bytes = FrameCodec.Encode(0, FrameKind.Data, "{}"u8).Concat(FrameCodec.Encode(5, FrameKind.Close, [])).ToArray();
        var buffer = new ReadOnlySequence<byte>(bytes);

        Assert.True(FrameCodec.TryReadFrame(ref buffer, out var first, out var firstPayload));
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out var second, out var secondPayload));
        Assert.False(FrameCodec.TryReadFrame(ref buffer, out _, out _));

        Assert.Equal((0u, FrameKind.Data), (first.Channel, first.Kind));
        Assert.Equal("{}", Encoding.UTF8.GetString(firstPayload.ToArray()));
        Assert.Equal((5u, FrameKind.Close, 0L), (second.Channel, second.Kind, secondPayload.Length));
        Assert.True(buffer.IsEmpty);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(12)]
    public void Waits_for_a_whole_frame_and_parses_it_across_segments(int split)
    {
        var bytes = FrameCodec.Encode(7, FrameKind.Data, "hello there"u8);

        var partial = new ReadOnlySequence<byte>(bytes.AsMemory(0, split));
        Assert.False(FrameCodec.TryReadFrame(ref partial, out _, out _));
        Assert.Equal(split, partial.Length); // untouched

        var whole = Segments(bytes[..split], bytes[split..]);
        Assert.True(FrameCodec.TryReadFrame(ref whole, out var header, out var payload));
        Assert.Equal(7u, header.Channel);
        Assert.Equal("hello there", Encoding.UTF8.GetString(payload.ToArray()));
    }

    [Fact]
    public void Rejects_a_payload_over_16_MiB()
    {
        var header = new byte[FrameCodec.HeaderSize];
        header[4] = (byte)FrameKind.Data;
        BitConverter.TryWriteBytes(header.AsSpan(5), (uint)FrameCodec.MaxPayload + 1);
        var buffer = new ReadOnlySequence<byte>(header);

        Assert.Throws<HerdrProtocolException>(() => FrameCodec.TryReadFrame(ref buffer, out _, out _));
    }

    [Fact]
    public void Reads_lines_and_drops_carriage_returns()
    {
        var buffer = new ReadOnlySequence<byte>("one\r\ntwo\nthr"u8.ToArray());

        Assert.True(FrameCodec.TryReadLine(ref buffer, out var one));
        Assert.True(FrameCodec.TryReadLine(ref buffer, out var two));
        Assert.False(FrameCodec.TryReadLine(ref buffer, out _));

        Assert.Equal("one", Encoding.UTF8.GetString(one.ToArray()));
        Assert.Equal("two", Encoding.UTF8.GetString(two.ToArray()));
        Assert.Equal("thr", Encoding.UTF8.GetString(buffer.ToArray()));
    }

    static ReadOnlySequence<byte> Segments(params byte[][] parts)
    {
        Segment? first = null, last = null;
        foreach (var part in parts)
        {
            var segment = new Segment(part, last);
            first ??= segment;
            last = segment;
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] bytes, Segment? previous)
        {
            Memory = bytes;
            if (previous is not null)
            {
                RunningIndex = previous.RunningIndex + previous.Memory.Length;
                previous.Next = this;
            }
        }
    }
}
