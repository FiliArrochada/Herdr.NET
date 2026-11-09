using System.Threading.Channels;
using HerdrNet.Model;

namespace HerdrNet.Test;

public class MirrorTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    const uint EventChannel = 3;

    sealed class Harness : IAsyncDisposable
    {
        public required HerdrConnection Connection { get; init; }
        public required FakeCore Core { get; init; }
        public required TerminalMirror Mirror { get; init; }
        public Channel<MirrorChange> Changes { get; } = Channel.CreateUnbounded<MirrorChange>();

        public Task SendAsync(params string[] lines) => Core.SendDataAsync(EventChannel, string.Concat(lines.Select(l => l + "\n")));

        public async Task<MirrorChange> NextAsync() => await Changes.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

        public async ValueTask DisposeAsync()
        {
            await Mirror.DisposeAsync();
            await Connection.DisposeAsync();
            await Core.DisposeAsync();
        }
    }

    static string Ev(long seq, string type, string terminal, string data) =>
        $$$"""{"jsonrpc":"2.0","method":"event","params":{"seq":{{{seq}}},"type":"{{{type}}}","terminal":"{{{terminal}}}","data":{{{data}}}}}""";

    static async Task<Harness> StartedAsync(string snapshot, IReadOnlyDictionary<string, string>? labels = null)
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        var harness = new Harness { Connection = connection, Core = core, Mirror = new TerminalMirror(connection, labels) };
        harness.Mirror.Changed += c => harness.Changes.Writer.TryWrite(c);
        var starting = harness.Mirror.StartAsync();
        var request = await core.ReadRequestAsync();
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), $$"""{"seq":10,"terminals":[{{snapshot}}],"channel":{{EventChannel}}}""");
        await starting.WaitAsync(Timeout);
        return harness;
    }

    [Fact]
    public async Task Applies_the_documented_event_table_in_order()
    {
        await using var h = await StartedAsync(ConnectionTests.Terminal("t7"));
        Assert.Equal(MirrorChangeKind.Reset, (await h.NextAsync()).Kind);
        Assert.Equal(10, h.Mirror.Seq);

        await h.SendAsync(
            Ev(11, "created", "t8", ConnectionTests.Terminal("t8")),
            Ev(12, "status", "t7", """{"status":{"state":"working"},"records":{},"reported":true}"""),
            Ev(13, "turn", "t7", """{"turn":4,"state":"done"}"""),
            Ev(14, "title", "t7", """{"title":"tests"}"""),
            Ev(15, "size", "t7", """{"cols":132,"rows":50}"""),
            Ev(16, "foreground", "t7", """{"pid":9,"name":"node","argv":["node"],"cwd":"/src"}"""),
            Ev(17, "labels", "t7", """{"labels":{"herdr.seen":"4"}}"""),
            Ev(18, "finished", "t8", """{"reason":"exited","exit_code":2,"signal":null,"finished_ms":99}"""),
            Ev(19, "removed", "t8", "{}"));

        Assert.Equal((MirrorChangeKind.Added, "t8"), await h.NextAsync() is var a ? (a.Kind, a.Address) : default);
        for (var i = 0; i < 6; i++)
            Assert.Equal(MirrorChangeKind.Updated, (await h.NextAsync()).Kind);
        var finished = await h.NextAsync();
        Assert.Equal((TerminalStates.Finished, 2, 99L), (finished.Terminal!.State, finished.Terminal.ExitCode, finished.Terminal.FinishedMs));
        Assert.Equal((MirrorChangeKind.Removed, "t8"), await h.NextAsync() is var r ? (r.Kind, r.Address) : default);

        var t7 = Assert.Single(h.Mirror.Terminals).Value;
        Assert.Equal(("working", 4L, "tests", 132, 50, "node", "4"),
            (t7.Status!.State, t7.Turn, t7.Title, t7.Cols, t7.Rows, t7.Foreground!.Name, t7.Labels["herdr.seen"]));
        Assert.Equal(19, h.Mirror.Seq);
    }

    [Fact]
    public async Task Lost_keeps_the_terminal_live_and_clears_its_process()
    {
        await using var h = await StartedAsync(ConnectionTests.Terminal("t7").Replace("\"keep\":false", "\"keep\":true,\"pid\":42"));
        await h.NextAsync();

        await h.SendAsync(Ev(11, "lost", "t7", """{"reason":"signaled","exit_code":null,"signal":9,"finished_ms":null}"""));
        var lost = (await h.NextAsync()).Terminal!;

        Assert.Equal((TerminalStates.Live, "signaled", (int?)null, (ForegroundProcess?)null), (lost.State, lost.Reason, lost.Pid, lost.Foreground));
    }

    [Fact]
    public async Task Stale_events_are_dropped_and_missed_replaces_everything()
    {
        await using var h = await StartedAsync(ConnectionTests.Terminal("t7"));
        await h.NextAsync();

        await h.SendAsync(
            Ev(9, "title", "t7", """{"title":"stale"}"""),
            """{"jsonrpc":"2.0","method":"missed","params":{"seq":30,"terminals":[""" + ConnectionTests.Terminal("t9") + "]}}",
            Ev(30, "title", "t9", """{"title":"also stale"}"""),
            Ev(31, "title", "t9", """{"title":"fresh"}"""));

        Assert.Equal(MirrorChangeKind.Reset, (await h.NextAsync()).Kind);
        var fresh = await h.NextAsync();
        Assert.Equal(("t9", "fresh"), (fresh.Address, fresh.Terminal!.Title));
        Assert.Equal("t9", Assert.Single(h.Mirror.Terminals).Key);
    }

    [Fact]
    public async Task With_a_label_filter_terminals_leave_and_join_on_labels_events()
    {
        var filter = new Dictionary<string, string> { ["peguwire.workspace"] = "w1" };
        await using var h = await StartedAsync(ConnectionTests.Terminal("t7").Replace("\"labels\":{}", "\"labels\":{\"peguwire.workspace\":\"w1\"}"), filter);
        await h.NextAsync();

        var joined = ConnectionTests.Terminal("t8").Replace("\"labels\":{}", "\"labels\":{\"peguwire.workspace\":\"w1\"}");
        await h.SendAsync(
            Ev(11, "labels", "t7", """{"labels":{"peguwire.workspace":"w2"}}"""),
            Ev(12, "labels", "t8", $$"""{"labels":{"peguwire.workspace":"w1"},"record":{{joined}}}"""));

        Assert.Equal((MirrorChangeKind.Removed, "t7"), await h.NextAsync() is var a ? (a.Kind, a.Address) : default);
        Assert.Equal((MirrorChangeKind.Added, "t8"), await h.NextAsync() is var b ? (b.Kind, b.Address) : default);
        Assert.Equal("t8", Assert.Single(h.Mirror.Terminals).Key);
    }

    [Fact]
    public async Task Doc_events_pass_through_and_a_throwing_handler_does_not_stop_the_mirror()
    {
        await using var h = await StartedAsync(ConnectionTests.Terminal("t7"));
        await h.NextAsync();
        h.Mirror.Changed += _ => throw new InvalidOperationException("handler bug");

        await h.SendAsync(
            """{"jsonrpc":"2.0","method":"event","params":{"seq":11,"type":"doc","data":{"name":"peguwire","version":2}}}""",
            Ev(12, "title", "t7", """{"title":"still applied"}"""));

        var doc = await h.NextAsync();
        Assert.Equal((MirrorChangeKind.DocumentChanged, "peguwire"), (doc.Kind, doc.Event!.AsDoc().Name));
        Assert.Equal("still applied", (await h.NextAsync()).Terminal!.Title);
    }

    [Fact]
    public async Task Completes_when_the_connection_is_lost()
    {
        await using var h = await StartedAsync(ConnectionTests.Terminal("t7"));
        await h.Core.DisconnectAsync();

        await h.Mirror.Completion.WaitAsync(Timeout);
        Assert.Single(h.Mirror.Terminals); // the last known state stays readable
    }
}
