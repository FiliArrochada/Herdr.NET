using System.Text;
using HerdrNet.Framing;
using HerdrNet.Model;

namespace HerdrNet.Test;

public class EventTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Subscribe_returns_the_snapshot_then_events_heartbeats_and_missed_in_order()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var subscribing = connection.Events.SubscribeAsync(labels: new Dictionary<string, string> { ["app.workspace"] = "w1" });
        var request = await core.ReadRequestAsync();
        Assert.Equal("w1", request.GetProperty("params").GetProperty("labels").GetProperty("app.workspace").GetString());
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), $$"""{"seq":41,"terminals":[{{ConnectionTests.Terminal("t7")}}],"channel":3}""");
        await using var subscription = await subscribing.WaitAsync(Timeout);

        Assert.Equal(41, subscription.Seq);
        Assert.Equal("t7", Assert.Single(subscription.Terminals).Address);

        // Two notifications in one frame, the third split across two frames.
        await core.SendDataAsync(3,
            """{"jsonrpc":"2.0","method":"event","params":{"seq":42,"type":"status","terminal":"t7","data":{"status":{"state":"blocked","kind":"permission"},"records":{},"reported":true}}}""" + "\n" +
            """{"jsonrpc":"2.0","method":"heartbeat","params":{"seq":42}}""" + "\n" +
            """{"jsonrpc":"2.0","method":"missed","params":{"seq":57,""");
        await core.SendDataAsync(3, "\"terminals\":[" + ConnectionTests.Terminal("t8") + "]}}\n");
        await core.SendFrameAsync(3, FrameKind.Close, """{"reason":"server_stop"}"""u8);

        var received = new List<HerdrNotification>();
        await foreach (var n in subscription.ReadAllAsync().WithCancellation(TestContext.Current.CancellationToken))
            received.Add(n);

        Assert.Collection(received,
            n =>
            {
                var e = Assert.IsType<HerdrEvent>(n);
                Assert.Equal((42L, HerdrEventTypes.Status, "t7"), (e.Seq, e.Type, e.Terminal));
                Assert.Equal("blocked", e.Data.GetProperty("status").GetProperty("state").GetString());
            },
            n => Assert.Equal(42, Assert.IsType<HerdrHeartbeat>(n).Seq),
            n => Assert.Equal("t8", Assert.Single(Assert.IsType<HerdrMissed>(n).Terminals).Address));
        await subscription.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Reading_events_returns_credit()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var subscribing = connection.Events.SubscribeAsync();
        var request = await core.ReadRequestAsync();
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), """{"seq":0,"terminals":[],"channel":4}""");
        await using var subscription = await subscribing.WaitAsync(Timeout);
        var line = """{"jsonrpc":"2.0","method":"heartbeat","params":{"seq":0}}""" + "\n";
        await core.SendDataAsync(4, line);

        await using var events = subscription.ReadAllAsync().GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout));
        Assert.IsType<HerdrHeartbeat>(events.Current);

        Assert.Equal((uint)Encoding.UTF8.GetByteCount(line), FakeCore.ReadCredit(await core.ReadFrameAsync(4, FrameKind.Credit)));
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","method":"event","params":{"seq":1,"type":"doc","machine":"rohan","data":{"name":"app-layout","version":2}}}""", "doc", null, "rohan")]
    [InlineData("""{"seq":2,"type":"removed","terminal":"t1","data":{}}""", "removed", "t1", null)]
    public void Parses_event_lines(string line, string type, string? terminal, string? machine)
    {
        var parsed = Assert.IsType<HerdrEvent>(EventSubscription.Parse(new System.Buffers.ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(line))));

        Assert.Equal((type, terminal, machine), (parsed.Type, parsed.Terminal, parsed.Machine));
    }

    [Fact]
    public void Unknown_notifications_are_kept_not_dropped()
    {
        var parsed = EventSubscription.Parse(new System.Buffers.ReadOnlySequence<byte>("""{"jsonrpc":"2.0","method":"future","params":{"x":1}}"""u8.ToArray()));

        Assert.Equal("future", Assert.IsType<HerdrUnknownNotification>(parsed).Method);
    }
}
