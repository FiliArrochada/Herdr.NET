using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using HerdrNet.Framing;
using HerdrNet.Model;

namespace HerdrNet.Test;

public class StreamTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    static async Task<(HerdrConnection, FakeCore, TerminalStream)> AttachedAsync(uint channel = 5, bool automaticInput = true, string firstPaint = "")
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        var attaching = connection.Terminals.AttachAsync("t7", new TerminalAttachOptions { Cols = 120, Rows = 40, History = 100 });
        var request = await core.ReadRequestAsync();
        var reply = FrameCodec.Encode(0, FrameKind.Data, Encoding.UTF8.GetBytes(
            $$$"""{"jsonrpc":"2.0","id":{{{request.GetProperty("id")}}},"result":{"channel":{{{channel}}},"terminal":{{{ConnectionTests.Terminal("t7")}}},"resize_notifications":false,"automatic_input":{{{(automaticInput ? "true" : "false")}}}}}"""));
        // The reply and the first paint arrive in the same write, as the core may send them.
        var paint = firstPaint.Length > 0 ? FrameCodec.Encode(channel, FrameKind.Data, Encoding.UTF8.GetBytes(firstPaint)) : [];
        await core.SendRawAsync([.. reply, .. paint]);
        var stream = await attaching.WaitAsync(Timeout);
        return (connection, core, stream);
    }

    [Fact]
    public async Task Attach_sends_size_and_history_and_receives_the_first_paint_sent_with_the_reply()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var attaching = connection.Terminals.AttachAsync("t7", new TerminalAttachOptions { Cols = 120, Rows = 40, History = 100 });
        var request = await core.ReadRequestAsync();
        var p = request.GetProperty("params");
        Assert.Equal("terminal.attach", request.GetProperty("method").GetString());
        Assert.Equal((120, 40, 100), (p.GetProperty("cols").GetInt32(), p.GetProperty("rows").GetInt32(), p.GetProperty("history").GetInt32()));
        Assert.False(p.TryGetProperty("observe", out var observe)); // unset options are omitted

        var reply = FrameCodec.Encode(0, FrameKind.Data, Encoding.UTF8.GetBytes(
            $$$"""{"jsonrpc":"2.0","id":{{{request.GetProperty("id")}}},"result":{"channel":9,"terminal":{{{ConnectionTests.Terminal("t7")}}},"resize_notifications":false,"automatic_input":false}}"""));
        var paint = FrameCodec.Encode(9, FrameKind.Data, "\e[?9001l\e[2Jprompt$ "u8);
        await core.SendRawAsync([.. reply, .. paint]);

        await using var stream = await attaching.WaitAsync(Timeout);
        Assert.Equal("t7", stream.Terminal);
        const string expected = "\e[?9001l\e[2Jprompt$ ";
        Assert.Equal(expected, await ReadTextAsync(stream, expected.Length));
    }

    [Fact]
    public async Task Consuming_output_returns_credit_for_exactly_the_bytes_consumed()
    {
        var (connection, core, stream) = await AttachedAsync(firstPaint: "0123456789");
        await using var _ = connection;
        await using var __ = core;

        var read = await stream.Output.ReadAsync().AsTask().WaitAsync(Timeout);
        Assert.Equal(10, read.Buffer.Length);
        stream.Output.AdvanceTo(read.Buffer.GetPosition(4)); // consume 4, below the batch: no credit yet
        stream.Output.AdvanceTo((await stream.Output.ReadAsync().AsTask().WaitAsync(Timeout)).Buffer.End); // drain the rest: credit for all

        var credit = await core.ReadFrameAsync(5, FrameKind.Credit);
        Assert.Equal(10u, FakeCore.ReadCredit(credit));
    }

    [Fact]
    public async Task Large_output_is_credited_in_batches()
    {
        var (connection, core, stream) = await AttachedAsync();
        await using var _ = connection;
        await using var __ = core;

        var chunk = new byte[ChannelEndpoint.CreditBatch];
        await core.SendFrameAsync(5, FrameKind.Data, chunk);
        await core.SendFrameAsync(5, FrameKind.Data, chunk);

        long consumed = 0;
        while (consumed < 2 * chunk.Length)
        {
            var read = await stream.Output.ReadAsync().AsTask().WaitAsync(Timeout);
            consumed += read.Buffer.Length;
            stream.Output.AdvanceTo(read.Buffer.End);
        }

        long credited = 0;
        while (credited < consumed)
            credited += FakeCore.ReadCredit(await core.ReadFrameAsync(5, FrameKind.Credit));
        Assert.Equal(consumed, credited);
    }

    [Fact]
    public async Task Input_resize_focus_and_facts_go_out_on_the_stream_channel()
    {
        var (connection, core, stream) = await AttachedAsync();
        await using var _ = connection;
        await using var __ = core;

        await stream.WriteInputAsync("ls\r"u8.ToArray());
        await stream.ResizeAsync(132, 43);
        await stream.FocusAsync(true);
        await stream.SetDisplayAsync(new Display { Fg = "#ffffff", Bg = "#000000", Cell = [9, 18] });

        Assert.Equal("ls\r", Encoding.UTF8.GetString(await core.ReadFrameAsync(5, FrameKind.Data)));
        var resize = await core.ReadFrameAsync(5, FrameKind.Resize);
        Assert.Equal((132, 43), (BinaryPrimitives.ReadUInt16LittleEndian(resize), BinaryPrimitives.ReadUInt16LittleEndian(resize.AsSpan(2))));
        Assert.Equal(new byte[] { 1 }, await core.ReadFrameAsync(5, FrameKind.Focus));
        Assert.Equal("""{"fg":"#ffffff","bg":"#000000","cell":[9,18]}""", Encoding.UTF8.GetString(await core.ReadFrameAsync(5, FrameKind.Facts)));
    }

    [Theory]
    [InlineData(true, (byte)FrameKind.AutomaticInput)]
    [InlineData(false, (byte)FrameKind.Data)]
    public async Task Automatic_input_uses_its_own_kind_only_when_negotiated(bool negotiated, byte expected)
    {
        var (connection, core, stream) = await AttachedAsync(automaticInput: negotiated);
        await using var _ = connection;
        await using var __ = core;

        await stream.WriteAutomaticInputAsync("\e[?1;2c"u8.ToArray());

        var (header, payload) = await core.ReadFrameAsync();
        Assert.Equal((5u, (FrameKind)expected), (header.Channel, header.Kind));
        Assert.Equal("\e[?1;2c", Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public async Task Program_exit_ends_the_stream_with_the_core_reason_after_the_last_output()
    {
        var (connection, core, stream) = await AttachedAsync();
        await using var _ = connection;
        await using var __ = core;

        await core.SendDataAsync(5, "bye\r\n");
        await core.SendFrameAsync(5, FrameKind.Close, """{"reason":"exited","exit_code":3,"signal":null}"""u8);

        var closed = await stream.Completion.WaitAsync(Timeout);
        Assert.Equal((TerminalCloseReasons.Exited, 3), (closed.Reason, closed.ExitCode));
        Assert.False(closed.TerminalMayStillRun);
        Assert.Equal("bye\r\n", await ReadToEndAsync(stream));
    }

    [Fact]
    public async Task Disposing_detaches_with_close_and_waits_for_the_answer()
    {
        var (connection, core, stream) = await AttachedAsync();
        await using var _ = connection;
        await using var __ = core;

        var detaching = stream.DisposeAsync().AsTask();
        Assert.Empty(await core.ReadFrameAsync(5, FrameKind.Close));
        await core.SendFrameAsync(5, FrameKind.Close, []);
        await detaching.WaitAsync(Timeout);

        var closed = await stream.Completion.WaitAsync(Timeout);
        Assert.Equal(TerminalCloseReasons.Detached, closed.Reason);
        Assert.True(closed.TerminalMayStillRun);
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public async Task Sending_after_detach_faults_the_task_instead_of_throwing()
    {
        var (connection, core, stream) = await AttachedAsync();
        await using var _ = connection;
        await using var __ = core;
        var detaching = stream.DisposeAsync().AsTask();
        await core.ReadFrameAsync(5, FrameKind.Close);
        await core.SendFrameAsync(5, FrameKind.Close, []);
        await detaching.WaitAsync(Timeout);

        // A UI's lost-focus or resize event can arrive after the view detached; the call itself must not throw.
        var focus = stream.FocusAsync(false);
        var resize = stream.ResizeAsync(80, 24);
        var input = stream.WriteInputAsync("x"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() => focus.AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => resize.AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => input.AsTask());
    }

    [Fact]
    public async Task Detaching_while_a_reader_holds_a_read_lets_it_finish_instead_of_throwing()
    {
        var (connection, core, stream) = await AttachedAsync(firstPaint: "last words");
        await using var _ = connection;
        await using var __ = core;

        // The consumer has a read in hand (not yet advanced) when another thread detaches: the race a
        // real core on Linux showed. Its AdvanceTo must still work and its next read must say "done".
        var held = await stream.Output.ReadAsync().AsTask().WaitAsync(Timeout);
        var detaching = stream.DisposeAsync().AsTask();
        await core.ReadFrameAsync(5, FrameKind.Close);
        await core.SendFrameAsync(5, FrameKind.Close, []);
        await detaching.WaitAsync(Timeout);

        Assert.Equal("last words", Encoding.UTF8.GetString(held.Buffer.ToArray()));
        stream.Output.AdvanceTo(held.Buffer.End);
        var after = await stream.Output.ReadAsync().AsTask().WaitAsync(Timeout);
        Assert.True(after.IsCompleted);
        Assert.True(after.Buffer.IsEmpty);
        stream.Output.AdvanceTo(after.Buffer.End);
        Assert.True(stream.Output.TryRead(out var again) && again.IsCompleted && again.Buffer.IsEmpty);
    }

    [Fact]
    public async Task Disposing_a_subscription_while_it_is_read_ends_the_reader_quietly()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var subscribing = connection.Events.SubscribeAsync();
        var request = await core.ReadRequestAsync();
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), """{"channel":12,"seq":0,"terminals":[]}""");
        var subscription = await subscribing.WaitAsync(Timeout);

        var reading = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var __ in subscription.ReadAllAsync())
                count++;
            return count;
        });
        await core.SendDataAsync(12, """{"jsonrpc":"2.0","method":"heartbeat","params":{"seq":0}}""" + "\n");

        var disposing = subscription.DisposeAsync().AsTask();
        await core.ReadFrameAsync(12, FrameKind.Close);
        await core.SendFrameAsync(12, FrameKind.Close, []);
        await disposing.WaitAsync(Timeout);

        Assert.InRange(await reading.WaitAsync(Timeout), 0, 1);
    }

    [Fact]
    public async Task Losing_the_connection_ends_streams_as_connection_lost()
    {
        var (connection, core, stream) = await AttachedAsync();
        await using var _ = connection;
        await using var __ = core;

        await core.DisconnectAsync();

        var closed = await stream.Completion.WaitAsync(Timeout);
        Assert.Equal(TerminalCloseReasons.ConnectionLost, closed.Reason);
        Assert.True(closed.TerminalMayStillRun);
        Assert.IsType<HerdrConnectionException>(closed.Error);
    }

    [Fact]
    public async Task A_cancelled_attach_closes_the_channel_its_late_reply_opens()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        using var cts = new CancellationTokenSource();
        var attaching = connection.Terminals.AttachAsync("t7", cancellationToken: cts.Token);
        var request = await core.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attaching.WaitAsync(Timeout));

        await core.ReplyAsync(request.GetProperty("id").GetInt64(), $$"""{"channel":11,"terminal":{{ConnectionTests.Terminal("t7")}},"resize_notifications":false,"automatic_input":false}""");

        Assert.Empty(await core.ReadFrameAsync(11, FrameKind.Close));
    }

    [Fact]
    public async Task Streams_are_independent_a_full_one_does_not_block_another_or_calls()
    {
        var (connection, core, stalled) = await AttachedAsync(channel: 5);
        await using var _ = connection;
        await using var __ = core;

        // Nobody reads the first stream.
        await core.SendFrameAsync(5, FrameKind.Data, new byte[256 * 1024]);

        var attaching = connection.Terminals.AttachAsync("t8");
        var request = await core.ReadRequestAsync();
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), $$"""{"channel":6,"terminal":{{ConnectionTests.Terminal("t8")}},"resize_notifications":false,"automatic_input":false}""");
        await using var other = await attaching.WaitAsync(Timeout);
        await core.SendDataAsync(6, "live");

        Assert.Equal("live", await ReadTextAsync(other, 4));
        GC.KeepAlive(stalled);
    }

    static async Task<string> ReadTextAsync(TerminalStream stream, int length)
    {
        var text = new StringBuilder();
        while (text.Length < length)
        {
            var read = await stream.Output.ReadAsync().AsTask().WaitAsync(Timeout);
            text.Append(Encoding.UTF8.GetString(read.Buffer.ToArray()));
            stream.Output.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                break;
        }

        return text.ToString();
    }

    static async Task<string> ReadToEndAsync(TerminalStream stream)
    {
        var text = new StringBuilder();
        while (true)
        {
            var read = await stream.Output.ReadAsync().AsTask().WaitAsync(Timeout);
            text.Append(Encoding.UTF8.GetString(read.Buffer.ToArray()));
            stream.Output.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                return text.ToString();
        }
    }
}
