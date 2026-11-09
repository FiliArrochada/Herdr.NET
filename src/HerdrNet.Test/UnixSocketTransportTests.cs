using System.Net.Sockets;
using HerdrNet.Transport;

namespace HerdrNet.Test;

/// <summary>
/// The direct transport over a real Unix domain socket: the path Linux and macOS use. Windows 10 and
/// later have AF_UNIX too, so this runs everywhere; a relay puts <see cref="FakeCore"/> behind the socket.
/// </summary>
public sealed class UnixSocketTransportTests : IAsyncDisposable
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // Short: sun_path holds about 100 bytes (104 on macOS, 108 on Linux).
    readonly string _path = Path.Combine(Path.GetTempPath(), $"hn-{Guid.NewGuid():N}"[..11] + ".sock");
    readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

    public UnixSocketTransportTests()
    {
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        _listener.Listen(1);
    }

    public ValueTask DisposeAsync()
    {
        _listener.Dispose();
        File.Delete(_path);
        return ValueTask.CompletedTask;
    }

    /// <summary>Accepts one client and copies bytes between it and the fake core's client end.</summary>
    async Task RelayAsync(FakeCore core)
    {
        using var accepted = await _listener.AcceptAsync();
        await using var socket = new NetworkStream(accepted, ownsSocket: false);
        await Task.WhenAny(socket.CopyToAsync(core.ClientStream), core.ClientStream.CopyToAsync(socket));
    }

    [Fact]
    public async Task Connects_over_a_unix_socket_and_calls_a_method()
    {
        await using var core = new FakeCore();
        var relay = RelayAsync(core);
        var transport = new EndpointTransport(new HerdrEndpoint(_path, HerdrEndpointKind.UnixSocket));

        var connecting = HerdrConnection.ConnectAsync(transport);
        var hello = await core.ReadLineAsync();
        await core.SendLineAsync($$"""{"jsonrpc":"2.0","id":{{hello.GetProperty("id")}},"result":{{FakeCore.DefaultHello}}}""");
        await using var connection = await connecting.WaitAsync(Timeout);
        Assert.Equal("fake", connection.Hello.Core.Name);

        // A framed call and its reply, both ways through the socket.
        var listing = connection.Terminals.ListAsync();
        var request = await core.ReadRequestAsync();
        await core.ReplyAsync(request.GetProperty("id").GetInt64(), """{"terminals":[],"hidden_finished":4,"machines":[]}""");
        Assert.Equal(4, (await listing.WaitAsync(Timeout)).HiddenFinished);

        await connection.DisposeAsync();
        await relay.WaitAsync(Timeout);
    }

    [Fact]
    public async Task No_listener_at_the_path_means_the_core_is_unavailable()
    {
        var missing = new EndpointTransport(new HerdrEndpoint(_path + ".none", HerdrEndpointKind.UnixSocket), TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<HerdrCoreUnavailableException>(() => missing.OpenAsync().AsTask());
        Assert.Contains(".none", error.Message);
    }
}
