using System.Text;
using HerdrNet.Framing;
using HerdrNet.Model;

namespace HerdrNet.Test;

public class ConnectionTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Hello_asks_for_generation_1_framed_and_keeps_the_reply()
    {
        await using var core = new FakeCore();
        var connecting = HerdrConnection.ConnectAsync(core.Transport);

        var hello = await core.ReadLineAsync();
        Assert.Equal("2.0", hello.GetProperty("jsonrpc").GetString());
        Assert.Equal("hello", hello.GetProperty("method").GetString());
        Assert.Equal(1, hello.GetProperty("params").GetProperty("generation").GetInt32());
        Assert.True(hello.GetProperty("params").GetProperty("framed").GetBoolean());

        await core.SendLineAsync($$"""{"jsonrpc":"2.0","id":{{hello.GetProperty("id")}},"result":{{FakeCore.DefaultHello}}}""");
        await using var connection = await connecting.WaitAsync(Timeout);

        Assert.Equal("0.1.18", connection.Hello.Version);
        Assert.Equal("c0ffee", connection.Hello.Core.Id);
        Assert.True(connection.Supports("automatic_input"));
        Assert.Null(connection.Hello.LoginSession);
    }

    [Fact]
    public async Task Hello_keeps_the_login_session_the_core_reports()
    {
        await using var core = new FakeCore();
        var connecting = HerdrConnection.ConnectAsync(core.Transport);
        var hello = await core.ReadLineAsync();
        var reply = FakeCore.DefaultHello.Replace("\"framed\":true", "\"framed\":true,\"login_session\":{\"id\":3,\"user\":501,\"gui\":true}");

        await core.SendLineAsync($$"""{"jsonrpc":"2.0","id":{{hello.GetProperty("id")}},"result":{{reply}}}""");
        await using var connection = await connecting.WaitAsync(Timeout);

        Assert.Equal(new LoginSession { Id = 3, User = 501, Gui = true }, connection.Hello.LoginSession);
    }

    [Fact]
    public async Task Frames_sent_in_the_same_write_as_the_hello_reply_are_read_as_frames()
    {
        await using var core = new FakeCore();
        var connecting = HerdrConnection.ConnectAsync(core.Transport);
        var hello = await core.ReadLineAsync();

        // The reply line and the first bytes of the next frame arrive in one write: the mode switch
        // falls mid-buffer, and those bytes must be kept for the framed reader.
        var line = Encoding.UTF8.GetBytes($$"""{"jsonrpc":"2.0","id":{{hello.GetProperty("id")}},"result":{{FakeCore.DefaultHello}}}""" + "\n");
        var frame = FrameCodec.Encode(0, FrameKind.Data, """{"jsonrpc":"2.0","id":2,"result":{"terminals":[],"hidden_finished":3,"machines":[]}}"""u8);
        await core.SendRawAsync([.. line, .. frame[..6]]);
        await using var connection = await connecting.WaitAsync(Timeout);

        var listing = connection.Terminals.ListAsync();
        Assert.Equal(2, (await core.ReadRequestAsync()).GetProperty("id").GetInt64());
        await core.SendRawAsync(frame[6..]);

        Assert.Equal(3, (await listing.WaitAsync(Timeout)).HiddenFinished);
    }

    [Fact]
    public async Task Wrong_generation_is_incompatible()
    {
        await using var core = new FakeCore();
        var connecting = HerdrConnection.ConnectAsync(core.Transport);
        var hello = await core.ReadLineAsync();
        await core.SendLineAsync($$$"""{"jsonrpc":"2.0","id":{{{hello.GetProperty("id")}}},"result":{"generation":2,"version":"9","core":{"id":"x","name":"x"},"framed":true,"methods":[],"features":[]}}""");

        await Assert.ThrowsAsync<HerdrIncompatibleException>(() => connecting.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Missing_required_method_is_incompatible()
    {
        await using var core = new FakeCore();
        var connecting = HerdrConnection.ConnectAsync(core.Transport, new HerdrConnectionOptions { RequiredMethods = ["doc.get"] });
        var hello = await core.ReadLineAsync();
        await core.SendLineAsync($$"""{"jsonrpc":"2.0","id":{{hello.GetProperty("id")}},"result":{{FakeCore.DefaultHello}}}""");

        var error = await Assert.ThrowsAsync<HerdrIncompatibleException>(() => connecting.WaitAsync(Timeout));
        Assert.Contains("doc.get", error.Message);
    }

    [Fact]
    public async Task Concurrent_calls_answered_out_of_order_reach_their_callers()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var first = connection.Terminals.GetAsync("t1");
        var second = connection.Terminals.GetAsync("t2");
        var a = await core.ReadRequestAsync();
        var b = await core.ReadRequestAsync();

        await core.ReplyAsync(b.GetProperty("id").GetInt64(), Terminal(b.GetProperty("params").GetProperty("terminal").GetString()!));
        await core.ReplyAsync(a.GetProperty("id").GetInt64(), Terminal(a.GetProperty("params").GetProperty("terminal").GetString()!));

        Assert.Equal("t1", (await first.WaitAsync(Timeout)).Address);
        Assert.Equal("t2", (await second.WaitAsync(Timeout)).Address);
    }

    [Fact]
    public async Task Rpc_errors_carry_kind_and_hint()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var call = connection.Terminals.GetAsync("t9");
        var request = await core.ReadRequestAsync();
        await core.ReplyErrorAsync(request.GetProperty("id").GetInt64(), "not_found", "no terminal t9", "see hc ls");

        var error = await Assert.ThrowsAsync<HerdrRpcException>(() => call.WaitAsync(Timeout));
        Assert.Equal(HerdrErrorKinds.NotFound, error.Kind);
        Assert.Equal("see hc ls", error.Hint);
        Assert.Equal(-32000, error.Code);
        Assert.Equal("terminal.get", error.Method);
    }

    [Fact]
    public async Task Cancelling_a_call_cancels_only_that_call()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        using var cts = new CancellationTokenSource();
        var cancelled = connection.Terminals.GetAsync("t1", cts.Token);
        await core.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Timeout));

        var next = connection.Terminals.GetAsync("t2");
        var request = await core.ReadRequestAsync();
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), Terminal("t2"));
        Assert.Equal("t2", (await next.WaitAsync(Timeout)).Address);
    }

    [Fact]
    public async Task Losing_the_connection_fails_pending_calls_and_completes_faulted()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var pending = connection.Terminals.ListAsync();
        await core.ReadRequestAsync();
        await core.DisconnectAsync();

        await Assert.ThrowsAsync<HerdrConnectionException>(() => pending.WaitAsync(Timeout));
        await Assert.ThrowsAsync<HerdrConnectionException>(() => connection.Completion.WaitAsync(Timeout));
        Assert.False(connection.IsConnected);
        await Assert.ThrowsAsync<HerdrConnectionException>(() => connection.Terminals.ListAsync());
    }

    [Fact]
    public async Task Disposing_completes_normally()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var __ = core;

        await connection.DisposeAsync();

        await connection.Completion.WaitAsync(Timeout);
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task Raw_calls_send_the_given_params()
    {
        var (connection, core) = await FakeCore.ConnectedAsync();
        await using var _ = connection;
        await using var __ = core;

        var call = connection.RawCallAsync("templates.list");
        var request = await core.ReadRequestAsync();
        Assert.Equal("templates.list", request.GetProperty("method").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, request.GetProperty("params").ValueKind);
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), """{"templates":[]}""");

        var result = await call.WaitAsync(Timeout);
        Assert.Equal(0, result.GetProperty("templates").GetArrayLength());
    }

    internal static string Terminal(string address) =>
        $$"""{"address":"{{address}}","labels":{},"argv":["sh"],"cwd":"/","state":"live","created_ms":1,"screen":"exact","cols":80,"rows":24,"turn":0,"title":"","reported":false,"keep":false}""";
}
