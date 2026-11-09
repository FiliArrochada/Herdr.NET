using System.Buffers;
using System.Text;
using HerdrNet.Framing;
using HerdrNet.Model;

namespace HerdrNet.Test;

public class MethodTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Send_writes_only_the_options_given_and_timeouts_in_seconds()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var sending = connection.Terminals.SendAsync("t7", "run the tests", new TerminalSendOptions { Wait = true, Timeout = TimeSpan.FromSeconds(90), If = "idle" });
        var request = await core.ReadRequestAsync();
        var p = request.GetProperty("params");
        Assert.Equal("terminal.send", request.GetProperty("method").GetString());
        Assert.Equal(("t7", "run the tests", true, 90.0, "idle"),
            (p.GetProperty("terminal").GetString(), p.GetProperty("text").GetString(), p.GetProperty("wait").GetBoolean(), p.GetProperty("timeout").GetDouble(), p.GetProperty("if").GetString()));
        Assert.False(p.TryGetProperty("force", out var ___));

        await core.ReplyAsync(request.GetProperty("id").GetInt64(),
            """{"turn":3,"status":"idle","started":true,"wait":{"terminal":"t7","ended":true,"reason":"status","turn":4,"status":"done","children":[]}}""");
        var result = await sending.WaitAsync(Timeout);

        Assert.Equal((3L, true, WaitReasons.Status, 4L), (result.Turn, result.Started, result.Wait!.Reason, result.Wait.Turn));
    }

    [Fact]
    public async Task Keys_interrupt_keep_and_remove_round_trip()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var keys = connection.Terminals.KeysAsync("t7", ["ctrl+c", "enter"]);
        var request = await core.ReadRequestAsync();
        Assert.Equal(["ctrl+c", "enter"], request.GetProperty("params").GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), """{"turn":2,"status":null}""");
        Assert.Equal(2, (await keys.WaitAsync(Timeout)).Turn);

        var interrupt = connection.Terminals.InterruptAsync("t7", TimeSpan.FromSeconds(3));
        request = await core.ReadRequestAsync();
        Assert.Equal(3.0, request.GetProperty("params").GetProperty("timeout").GetDouble());
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), """{"outcome":"stopped","key":"esc"}""");
        Assert.Equal(("stopped", "esc"), (await interrupt.WaitAsync(Timeout)) is var i ? (i.Outcome, i.Key) : default);

        var keep = connection.Terminals.KeepAsync("t7", true);
        request = await core.ReadRequestAsync();
        Assert.True(request.GetProperty("params").GetProperty("keep").GetBoolean());
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), ConnectionTests.Terminal("t7"));
        Assert.Equal("t7", (await keep.WaitAsync(Timeout)).Address);

        var rm = connection.Terminals.RemoveAsync("t2");
        request = await core.ReadRequestAsync();
        Assert.Equal("terminal.rm", request.GetProperty("method").GetString());
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), """{"removed":"t2"}""");
        Assert.Equal("t2", (await rm.WaitAsync(Timeout)).Removed);
    }

    [Fact]
    public async Task Wait_templates_and_machines_parse_their_replies()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var wait = connection.Terminals.WaitAsync(new TerminalWaitOptions { Children = "all", Terminal = "t1" });
        var request = await core.ReadRequestAsync();
        Assert.Equal("all", request.GetProperty("params").GetProperty("children").GetString());
        await core.ReplyAsync(request.GetProperty("id").GetInt64(),
            """{"terminal":"t1","ended":true,"reason":"status","turn":0,"children":[{"terminal":"t4","ended":true,"reason":"exit","turn":1,"exit_code":0,"children":[]}]}""");
        var waited = await wait.WaitAsync(Timeout);
        Assert.Equal(("t4", 0), (waited.Children[0].Terminal, waited.Children[0].ExitCode));

        var templates = connection.Templates.ListAsync();
        request = await core.ReadRequestAsync();
        Assert.Equal("templates.list", request.GetProperty("method").GetString());
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), """{"templates":[{"name":"reviewer","description":"Reviews a diff","command":"claude"}]}""");
        Assert.Equal("reviewer", Assert.Single((await templates.WaitAsync(Timeout)).Templates).Name);

        var machines = connection.Machines.ListAsync();
        request = await core.ReadRequestAsync();
        await core.ReplyAsync(request.GetProperty("id").GetInt64(),
            """{"machines":[{"name":"rohan","target":"me@rohan","id":"m1","host":"rohan","version":"0.1.18","state":"connected","temporary":false,"via":false}]}""");
        Assert.Equal(("rohan", "connected"), Assert.Single((await machines.WaitAsync(Timeout)).Machines) is var m ? (m.Name, m.State) : default);

        var dial = connection.Machines.ConnectAsync("rohan");
        request = await core.ReadRequestAsync();
        Assert.Equal("machine.connect", request.GetProperty("method").GetString());
        Assert.Equal("rohan", request.GetProperty("params").GetProperty("machine").GetString());
        await core.ReplyAsync(request.GetProperty("id").GetInt64(),
            """{"name":"rohan","target":"me@rohan","id":"m1","host":"rohan","version":"0.1.18","state":"unreachable","since_ms":1700000000000,"why":"ssh: connect refused"}""");
        var line = await dial.WaitAsync(Timeout);
        Assert.Equal(("unreachable", "ssh: connect refused", 1700000000000L), (line.State, line.Why, line.SinceMs));
    }

    [Fact]
    public async Task A_resize_is_raised_before_the_bytes_that_follow_it_even_in_one_buffer()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var attaching = connection.Terminals.AttachAsync("t7", new TerminalAttachOptions { Cols = 80, Rows = 24, ResizeNotifications = true });
        var request = await core.ReadRequestAsync();
        Assert.True(request.GetProperty("params").GetProperty("resize_notifications").GetBoolean());
        var resize = new byte[] { 132, 0, 50, 0 };
        // Reply, old output, RESIZE, new paint: all in one write, so the client sees them in one buffer.
        await core.SendRawAsync([
            .. FrameCodec.Encode(0, FrameKind.Data, Encoding.UTF8.GetBytes(
                $$$"""{"jsonrpc":"2.0","id":{{{request.GetProperty("id")}}},"result":{"channel":5,"terminal":{{{ConnectionTests.Terminal("t7")}}},"resize_notifications":true,"automatic_input":true}}""")),
            .. FrameCodec.Encode(5, FrameKind.Data, "old"u8),
            .. FrameCodec.Encode(5, FrameKind.Resize, resize),
            .. FrameCodec.Encode(5, FrameKind.Data, "\u0018new"u8),
        ]);
        await using var stream = await attaching.WaitAsync(Timeout);
        Assert.True(stream.Attach.ResizeNotifications);

        var log = new List<string>();
        stream.Resized += (c, r) => log.Add($"resize {c}x{r}");
        var text = new StringBuilder();
        while (text.Length < 7)
        {
            var read = await stream.Output.ReadAsync().AsTask().WaitAsync(Timeout);
            var chunk = Encoding.UTF8.GetString(read.Buffer.ToArray());
            log.Add(chunk);
            text.Append(chunk);
            stream.Output.AdvanceTo(read.Buffer.End);
        }

        Assert.Equal(["old", "resize 132x50", "\u0018new"], log);
    }

    [Fact]
    public void Event_payloads_read_as_typed_records()
    {
        static HerdrEvent Parse(string line) =>
            Assert.IsType<HerdrEvent>(EventSubscription.Parse(new System.Buffers.ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(line))));

        var status = Parse("""{"seq":1,"type":"status","terminal":"t7","data":{"status":{"state":"blocked","kind":"permission","msg":"Allow?"},"records":{},"reported":true}}""").AsStatus();
        Assert.Equal(("blocked", "permission", "Allow?", true), (status.Status!.State, status.Status.Kind, status.Status.Message, status.Reported));

        Assert.Null(Parse("""{"seq":2,"type":"status","terminal":"t7","data":{"status":null,"records":{},"reported":true}}""").AsStatus().Status);
        Assert.Equal((5L, "done"), Parse("""{"seq":3,"type":"turn","terminal":"t7","data":{"turn":5,"state":"done"}}""").AsTurn() is var t ? (t.Turn, t.State) : default);
        Assert.Equal((100, 30), Parse("""{"seq":4,"type":"size","terminal":"t7","data":{"cols":100,"rows":30}}""").AsSize() is var z ? (z.Cols, z.Rows) : default);
        Assert.Equal(("exited", 0), Parse("""{"seq":5,"type":"finished","terminal":"t7","data":{"reason":"exited","exit_code":0,"signal":null,"finished_ms":9}}""").AsEnded() is var f ? (f.Reason, f.ExitCode) : default);
        Assert.Null(Parse("""{"seq":6,"type":"foreground","terminal":"t7","data":null}""").AsForeground());
        Assert.Equal(("peguwire", 3UL), Parse("""{"seq":7,"type":"doc","data":{"name":"peguwire","version":3}}""").AsDoc() is var d ? (d.Name, d.Version) : default);

        Assert.Throws<InvalidOperationException>(() => Parse("""{"seq":8,"type":"title","terminal":"t7","data":{"title":"x"}}""").AsSize());
    }
}
