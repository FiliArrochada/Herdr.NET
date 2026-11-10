using System.Collections.Concurrent;
using System.Threading.Channels;
using HerdrNet.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HerdrNet.Test;

public class SupervisorTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    sealed class NothingListens : IHerdrTransport
    {
        public string Description => "pipe nobody listens on";
        public ValueTask<Stream> OpenAsync(CancellationToken cancellationToken = default) => throw new HerdrCoreUnavailableException(Description);
    }

    /// <summary>A supervisor whose direct endpoint is dead and whose "hc connect" opens the next queued fake core.</summary>
    sealed class Harness : IAsyncDisposable
    {
        /// <summary>Reached through "hc connect" (which would start a stopped core).</summary>
        public ConcurrentQueue<FakeCore> Cores { get; } = new();
        /// <summary>Listening on the direct endpoint (a core that runs).</summary>
        public ConcurrentQueue<FakeCore> DirectCores { get; } = new();
        public int HcConnectOpens;
        public bool HcInstalled { get; set; } = true;
        public Channel<HerdrConnectionStateChange> States { get; } = Channel.CreateUnbounded<HerdrConnectionStateChange>();
        public ConnectionSupervisor Supervisor { get; }

        public int DirectOpens;

        public Harness(bool mirror = false, Action<HerdrOptions>? configure = null)
        {
            var options = new HerdrOptions { MirrorTerminals = mirror, ReconnectDelays = [TimeSpan.FromMilliseconds(10)] };
            configure?.Invoke(options);
            Supervisor = new ConnectionSupervisor(options, kind => kind switch
            {
                HerdrTransportPreference.Direct => Interlocked.Increment(ref DirectOpens) > 0 && DirectCores.TryDequeue(out var running) ? running.Transport : new NothingListens(),
                _ when !HcInstalled => null,
                _ => Cores.TryDequeue(out var core) ? Counted(core.Transport) : new NothingListens(),
            }, null);
            Supervisor.StateChanged += s => States.Writer.TryWrite(s);
        }

        IHerdrTransport Counted(IHerdrTransport transport)
        {
            Interlocked.Increment(ref HcConnectOpens);
            return transport;
        }

        public FakeCore QueueDirect()
        {
            var core = new FakeCore();
            DirectCores.Enqueue(core);
            return core;
        }

        public FakeCore Queue()
        {
            var core = new FakeCore();
            Cores.Enqueue(core);
            return core;
        }

        public async Task<HerdrConnectionStateChange> WaitForAsync(HerdrConnectionState state)
        {
            while (true)
            {
                var change = await States.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
                if (change.State == state)
                    return change;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            foreach (var core in Cores)
                await core.DisposeAsync();
        }
    }

    static async Task AnswerHelloAsync(FakeCore core, string hello = FakeCore.DefaultHello)
    {
        var request = await core.ReadLineAsync();
        await core.SendLineAsync($$"""{"jsonrpc":"2.0","id":{{request.GetProperty("id").GetInt64()}},"result":{{hello}}}""");
    }

    [Fact]
    public async Task Falls_back_to_hc_connect_when_nothing_listens_on_the_endpoint()
    {
        await using var h = new Harness();
        var core = h.Queue();
        h.Supervisor.Start();

        await AnswerHelloAsync(core);
        await h.WaitForAsync(HerdrConnectionState.Connected);

        Assert.Same(h.Supervisor.Current, await h.Supervisor.WaitForConnectionAsync().WaitAsync(Timeout));
        Assert.Equal("fake", h.Supervisor.Current!.Hello.Core.Name);
        await core.DisposeAsync();
    }

    [Fact]
    public async Task Reconnects_after_the_connection_is_lost()
    {
        await using var h = new Harness();
        var first = h.Queue();
        h.Supervisor.Start();
        await AnswerHelloAsync(first);
        await h.WaitForAsync(HerdrConnectionState.Connected);

        var second = h.QueueDirect(); // the core still runs: the endpoint answers again
        await first.DisconnectAsync();
        var lost = await h.WaitForAsync(HerdrConnectionState.Reconnecting);
        await AnswerHelloAsync(second);
        await h.WaitForAsync(HerdrConnectionState.Connected);

        Assert.True(h.Supervisor.Current!.IsConnected);
        Assert.IsType<HerdrConnectionException>(lost.Error);
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task After_a_loss_it_never_starts_a_stopped_core_until_asked()
    {
        await using var h = new Harness();
        var first = h.Queue();
        h.Supervisor.Start();
        await AnswerHelloAsync(first);
        await h.WaitForAsync(HerdrConnectionState.Connected);
        Assert.Equal(1, h.HcConnectOpens); // starting the app may start the core

        var restarted = h.Queue();
        await first.DisconnectAsync(); // the core was stopped: nothing listens any more
        var stopped = await h.WaitForAsync(HerdrConnectionState.CoreStopped);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.IsType<HerdrCoreUnavailableException>(stopped.Error);
        Assert.Equal(1, h.HcConnectOpens); // hc connect was not run again
        Assert.Equal(HerdrConnectionState.CoreStopped, h.Supervisor.State); // steady across the attempts

        h.Supervisor.RetryNow(startCore: true); // the person asked to start it
        await AnswerHelloAsync(restarted);
        await h.WaitForAsync(HerdrConnectionState.Connected);
        Assert.Equal(2, h.HcConnectOpens);
        await first.DisposeAsync();
        await restarted.DisposeAsync();
    }

    [Fact]
    public async Task A_command_is_the_only_way_in()
    {
        await using var h = new Harness(configure: o => o.Command = ["ssh", "box", "hc", "connect", "--session", "work"]);
        var core = h.Queue();
        h.Supervisor.Start();

        await AnswerHelloAsync(core);
        await h.WaitForAsync(HerdrConnectionState.Connected);

        Assert.Equal((1, 0), (h.HcConnectOpens, h.DirectOpens)); // this machine's endpoint is never tried
        await core.DisposeAsync();
    }

    [Fact]
    public async Task A_carrier_is_the_only_way_in_and_waits_for_the_person_after_a_loss()
    {
        var first = new FakeCore();
        var next = new FakeCore();
        var carrier = new QueuedTransport(first.Transport, next.Transport);
        await using var supervisor = new ConnectionSupervisor(new HerdrOptions
        {
            Carrier = carrier, MirrorTerminals = false, ReconnectDelays = [TimeSpan.FromMilliseconds(10)],
            // Never used: a carrier wins over a command, and this machine's core is never tried.
            Command = ["ssh", "nowhere", "hc", "connect"],
        });
        var states = Channel.CreateUnbounded<HerdrConnectionState>();
        supervisor.StateChanged += s => states.Writer.TryWrite(s.State);
        supervisor.Start();
        await AnswerHelloAsync(first);
        await WaitAsync(states, HerdrConnectionState.Connected);

        await first.DisconnectAsync();
        await WaitAsync(states, HerdrConnectionState.Disconnected);
        supervisor.RetryNow(startCore: true);
        await AnswerHelloAsync(next);
        await WaitAsync(states, HerdrConnectionState.Connected);

        Assert.Equal(2, carrier.Opens);
        await first.DisposeAsync();
        await next.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_leaves_a_live_connection_and_opens_the_carrier_again()
    {
        var first = new FakeCore();
        var next = new FakeCore();
        var carrier = new QueuedTransport(first.Transport, next.Transport);
        await using var supervisor = new ConnectionSupervisor(new HerdrOptions
        {
            Carrier = carrier, MirrorTerminals = false, ReconnectDelays = [TimeSpan.FromMilliseconds(10)],
        });
        var states = Channel.CreateUnbounded<HerdrConnectionState>();
        supervisor.StateChanged += s => states.Writer.TryWrite(s.State);
        supervisor.Start();
        await AnswerHelloAsync(first);
        await WaitAsync(states, HerdrConnectionState.Connected);

        supervisor.Reconnect(startCore: true); // e.g. another computer was chosen
        await AnswerHelloAsync(next);
        await WaitAsync(states, HerdrConnectionState.Connected);

        Assert.Equal(2, carrier.Opens);
        Assert.Same(supervisor.Current, await supervisor.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await first.DisposeAsync();
        await next.DisposeAsync();
    }

    [Fact]
    public async Task With_FirstAttempts_a_place_never_reached_stops_trying_until_asked()
    {
        var away = new AwayTransport();
        await using var supervisor = new ConnectionSupervisor(new HerdrOptions
        {
            Carrier = away, MirrorTerminals = false, FirstAttempts = 3, ReconnectDelays = [TimeSpan.FromMilliseconds(5)],
        });
        supervisor.Start();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(3, away.Opens);                      // then it waits, however long
        Assert.Null(supervisor.StateDetails.NextAttemptIn);

        supervisor.RetryNow(startCore: true);              // the person asked
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(4, away.Opens);                      // one more try, then waiting again
    }

    /// <summary>A carrier whose far end never answers.</summary>
    sealed class AwayTransport : IHerdrTransport
    {
        public int Opens;
        public string Description => "away";

        public ValueTask<Stream> OpenAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Opens);
            throw new IOException("No route to host.");
        }
    }

    /// <summary>A carrier that opens the next queued transport's stream on each attempt.</summary>
    sealed class QueuedTransport(params IHerdrTransport[] transports) : IHerdrTransport
    {
        public int Opens;
        public string Description => "carrier";
        public ValueTask<Stream> OpenAsync(CancellationToken cancellationToken = default) =>
            transports[Interlocked.Increment(ref Opens) - 1].OpenAsync(cancellationToken);
    }

    static async Task WaitAsync(Channel<HerdrConnectionState> states, HerdrConnectionState wanted)
    {
        while (await states.Reader.ReadAsync().AsTask().WaitAsync(Timeout) != wanted)
        {
        }
    }

    [Theory]
    [InlineData(true)]  // a Command (ssh box hc connect)
    [InlineData(false)] // Transport = HcConnect
    public async Task After_a_loss_a_connection_that_may_start_a_core_waits_for_the_person(bool command)
    {
        await using var h = new Harness(configure: o =>
        {
            if (command)
                o.Command = ["ssh", "box", "hc", "connect"];
            else
                o.Transport = HerdrTransportPreference.HcConnect;
        });
        var first = h.Queue();
        h.Supervisor.Start();
        await AnswerHelloAsync(first);
        await h.WaitForAsync(HerdrConnectionState.Connected);

        var next = h.Queue();
        await first.DisconnectAsync(); // a dropped line or a stopped core: no way to tell without starting one
        var disconnected = await h.WaitForAsync(HerdrConnectionState.Disconnected);
        Assert.IsType<HerdrConnectionException>(disconnected.Error);
        h.Supervisor.RetryNow(); // a plain retry is not the person asking to start anything
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, h.HcConnectOpens);
        Assert.Equal(HerdrConnectionState.Disconnected, h.Supervisor.State);

        h.Supervisor.RetryNow(startCore: true);
        await AnswerHelloAsync(next);
        await h.WaitForAsync(HerdrConnectionState.Connected);
        Assert.Equal((2, 0), (h.HcConnectOpens, h.DirectOpens));
        await first.DisposeAsync();
        await next.DisposeAsync();
    }

    [Fact]
    public async Task A_reconnect_the_person_asked_for_is_one_attempt()
    {
        await using var h = new Harness(configure: o => o.Command = ["ssh", "box", "hc", "connect"]);
        var first = h.Queue();
        h.Supervisor.Start();
        await AnswerHelloAsync(first);
        await h.WaitForAsync(HerdrConnectionState.Connected);
        await first.DisconnectAsync();
        await h.WaitForAsync(HerdrConnectionState.Disconnected);

        h.Supervisor.RetryNow(startCore: true); // the line is still down: nothing answers
        var failed = await h.WaitForAsync(HerdrConnectionState.Disconnected);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.IsType<HerdrCoreUnavailableException>(failed.Error);
        Assert.Equal(HerdrConnectionState.Disconnected, h.Supervisor.State); // no loop of its own
        await first.DisposeAsync();
    }

    [Fact]
    public async Task Reports_not_installed_until_hc_appears_and_retry_is_asked()
    {
        await using var h = new Harness { HcInstalled = false };
        h.Supervisor.Start();

        var notInstalled = await h.WaitForAsync(HerdrConnectionState.NotInstalled);
        Assert.IsType<HcNotFoundException>(notInstalled.Error);

        h.HcInstalled = true;
        var core = h.Queue();
        h.Supervisor.RetryNow();
        await AnswerHelloAsync(core);
        await h.WaitForAsync(HerdrConnectionState.Connected);
        await core.DisposeAsync();
    }

    [Fact]
    public async Task An_incompatible_core_waits_for_retry_instead_of_looping()
    {
        await using var h = new Harness();
        var core = h.Queue();
        h.Supervisor.Start();

        await AnswerHelloAsync(core, FakeCore.DefaultHello.Replace("\"generation\":1", "\"generation\":2"));
        await h.WaitForAsync(HerdrConnectionState.Incompatible);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(HerdrConnectionState.Incompatible, h.Supervisor.State); // no retry by itself

        var next = h.Queue();
        h.Supervisor.RetryNow();
        await AnswerHelloAsync(next);
        await h.WaitForAsync(HerdrConnectionState.Connected);
        await core.DisposeAsync();
        await next.DisposeAsync();
    }

    [Fact]
    public async Task Starts_a_mirror_with_each_connection_before_reporting_connected()
    {
        await using var h = new Harness(mirror: true);
        var core = h.Queue();
        h.Supervisor.Start();

        await AnswerHelloAsync(core);
        var subscribe = await core.ReadRequestAsync();
        Assert.Equal("events.subscribe", subscribe.GetProperty("method").GetString());
        await core.ReplyAsync(subscribe.GetProperty("id").GetInt64(), $$"""{"seq":1,"terminals":[{{ConnectionTests.Terminal("t7")}}],"channel":3}""");
        await h.WaitForAsync(HerdrConnectionState.Connected);

        Assert.Equal("t7", Assert.Single(h.Supervisor.Mirror!.Terminals).Key);
        await core.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_closes_the_connection_and_sends_nothing_else()
    {
        var h = new Harness();
        var core = h.Queue();
        h.Supervisor.Start();
        await AnswerHelloAsync(core);
        await h.WaitForAsync(HerdrConnectionState.Connected);

        await h.Supervisor.DisposeAsync();

        Assert.Equal(HerdrConnectionState.Stopped, h.Supervisor.State);
        await Assert.ThrowsAsync<EndOfStreamException>(core.ReadFrameAsync); // no request at all: in particular no server.stop
        await core.DisposeAsync();
    }

    [Fact]
    public async Task AddHerdr_registers_one_supervisor_and_one_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddHerdr(o => o.Session = "scratch").AddHerdr();
        await using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<ConnectionSupervisor>(), provider.GetRequiredService<ConnectionSupervisor>());
        Assert.Single(provider.GetServices<IHostedService>());
        Assert.Equal(HerdrConnectionState.Stopped, provider.GetRequiredService<ConnectionSupervisor>().State);
    }
}
