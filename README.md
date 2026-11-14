# HerdrNet

A hand-maintained, strongly typed, asynchronous .NET client for
[herdr-core](https://github.com/herdrdev/herdr-core) (`hc`), the terminal runtime that keeps
programs and coding agents alive after their viewers leave. It speaks the core's own protocol, with
no HTTP layer and no UI dependency:

- JSON-RPC 2.0 over the core's Unix socket or Windows named pipe, or over `hc connect` (stdio, also
  through SSH), switched to **framed mode** right after `hello`;
- concurrent calls on channel 0, correlated by id;
- **terminal streams** (`terminal.attach`) as a `PipeReader` of raw VT bytes, with the core's
  credit-based flow control driven by how far you read;
- **event subscriptions** as an `IAsyncEnumerable` of typed notifications;
- a **`TerminalMirror`** that keeps a live copy of the terminals from those events, and a
  **`ConnectionSupervisor`** that reconnects with backoff (`AddHerdr()` registers it in a host).

Not affiliated with the herdr project. "herdr" names the software this library talks to.

| | |
|---|---|
| API version | herdr-core **0.1.21**, protocol generation 1 ([`api/herdr-core-0.1.21.openrpc.json`](api/herdr-core-0.1.21.openrpc.json)) |
| Package version | `0.1.21` = the API version; a 4th component is a client-only revision against the same API |
| Framework | `net10.0` |
| Dependencies | `Microsoft.Extensions.Logging.Abstractions`, `.DependencyInjection.Abstractions`, `.Hosting.Abstractions`, `.Options` |
| Status | preview: unit-tested against a scripted core; probe passes against hc 0.1.21 on Windows and hc 0.1.20 on Debian 13 (both transports) |

The models are written by hand and kept in sync with the vendored OpenRPC document; contract tests
fail when a wire name the client uses is not in the schema, or a field the schema requires is not
modelled. Methods not modelled yet are reachable through `RawCallAsync`.

## Usage

```csharp
await using var core = await HerdrConnection.ConnectAsync();   // HC_SESSION, else "default"
Console.WriteLine($"herdr-core {core.Hello.Version} on {core.Hello.Core.Name}");

foreach (var t in (await core.Terminals.ListAsync()).Terminals)
    Console.WriteLine($"{t.Address} {t.Name} {t.Status?.State ?? "no status"}");

// Attach: the first paint, then raw VT output. Feed it to a terminal emulator, not a line reader.
await using var stream = await core.Terminals.AttachAsync("t7", new TerminalAttachOptions { Cols = 120, Rows = 40, History = 500 });
await stream.WriteInputAsync("ls\r"u8.ToArray());
var read = await stream.Output.ReadAsync();
emulator.Feed(read.Buffer);
stream.Output.AdvanceTo(read.Buffer.End);   // returns credit to the core
// Disposing detaches; the program keeps running.

// Events: snapshot, then changes in order.
await using var events = await core.Events.SubscribeAsync();
await foreach (var n in events.ReadAllAsync(ct))
    if (n is HerdrEvent { Type: HerdrEventTypes.Status } e) Console.WriteLine($"{e.Terminal}: {e.Data}");
```

A live list, kept up to date (what a dashboard binds to):

```csharp
var mirror = new TerminalMirror(core, labels: new Dictionary<string, string> { ["app.workspace"] = "w1" });
mirror.Changed += change => ui.Post(() => Refresh(change));   // raised on the mirror's task, in order
await mirror.StartAsync();                                       // first change is a Reset with the snapshot
var agents = mirror.Terminals.Values.Where(t => t.Status is not null);
```

One connection kept alive, in a host:

```csharp
builder.Services.AddHerdr(o => { o.Session = "work"; o.MirrorLabels = null; });
// ...
var supervisor = host.Services.GetRequiredService<ConnectionSupervisor>();
supervisor.StateChanged += s => Show(s.State, s.Error, s.NextAttemptIn);   // Connecting, Connected, Reconnecting, NotInstalled, Incompatible, CoreStopped, Disconnected
var connection = await supervisor.WaitForConnectionAsync();               // supervisor.Mirror is ready too
```

The supervisor connects to the core's endpoint and, when nothing listens there, through `hc connect`;
after a loss it retries at 0.5, 1, 2, then every 5 s. Streams do not survive a reconnect: attach
again (with `Resumed = true`) when the state turns `Connected`.

A core on another machine, through any command that carries the protocol on its stdio:

```csharp
builder.Services.AddHerdr(o => o.Command = ["ssh", "box", "hc", "connect", "--session", "work"]);
// After a loss: State == Disconnected until the person asks.
supervisor.RetryNow(startCore: true);   // runs the command once more
```

`CommandEnvironment` sets variables for the command's process only (null removes one), such as
`SSH_ASKPASS` and `SSH_ASKPASS_REQUIRE=force` so ssh asks for a password or a new host key through
your app instead of a terminal it does not have. Its values are never logged. Give such a command a
longer `Connection.HandshakeTimeout`: the handshake waits while the person answers.

Where no program can be started (a phone), open the stream yourself, such as an SSH channel to
`hc connect`, and pass it as a `Carrier`: an `IHerdrTransport` that opens a fresh stream on each
attempt. It follows the same rules as `Command`, which it replaces.

```csharp
builder.Services.AddHerdr(o => o.Carrier = new MySshChannelTransport(...));
```

`hc connect` starts a stopped core, and nothing tells a stopped core from a dropped line without
running it, so a connection only `hc connect` or a command can make (`Command`, or
`Transport = HcConnect`) is made by itself only the first time: after a loss the supervisor waits in
`Disconnected`, and each `RetryNow(startCore: true)` is one attempt. A command's `Description`
(logs, error messages) names only its program.

Machines the core added (`hc machine add`): `core.Machines.ListAsync()`, and
`core.Machines.ConnectAsync("rohan")` to dial one again now. Their terminals are in the same list
and event stream as `rohan/t7`.

Other transports:

```csharp
// Through `hc connect` (no endpoint rules; `new HcConnectTransport("ssh", ["box", "hc", "connect"])` reaches another machine):
var hc = HcLocator.Find() ?? throw new InvalidOperationException("hc is not installed");
await using var viaCli = await HerdrConnection.ConnectAsync(new HcConnectTransport(hc, session: "scratch"));
```

What the client deliberately does **not** do: stop or restart the core, or derive an agent's state
from screen text. Status is whatever the program reported through OSC 7501 (`Terminal.Status`).

## Probe

`samples/HerdrNet.Probe` runs the protocol end to end against a real core and prints a table of
what it measured: connect, handshake, list, subscribe, run a shell, attach, type, resize, detach,
reattach, then the same terminal again from a brand-new connection (the client-restart case),
reattach latency over 20 runs, the mirror against `terminal.get`, `templates.list`, `machine.list`,
`terminal.send`, an observer's `resize_notifications`, the supervisor, and with `--flood` the
stream throughput. It uses its own session (`herdrnet-probe`) so it never
touches your terminals, and kills (then removes) only the terminals it started.

```sh
HC_SESSION=herdrnet-probe hc ls        # starts that session's core
dotnet run --project samples/HerdrNet.Probe -- --session herdrnet-probe --flood
dotnet run --project samples/HerdrNet.Probe -- --session herdrnet-probe --hc auto   # via hc connect
```

## Build and test

```sh
dotnet build HerdrNet.sln
dotnet test HerdrNet.sln
```

The unit tests never start a real core: `FakeCore` is a scripted core on an in-memory duplex pipe,
which lets them check byte-exact framing, the hello/framed switch mid-buffer, out-of-order replies,
the attach reply arriving in the same write as the first paint, credit accounting, cancellation,
connection loss, the mirror's event table, and the supervisor's reconnect and fallback.

## Notes

- **Endpoints.** Names follow herdr-core's source
  (`\\.\pipe\herdr-core-<user>-<session>.sock` on Windows; `$XDG_RUNTIME_DIR/herdr-core/<session>.sock`,
  then `/run/user/<uid>/…`, then the session's state folder on Unix). The Windows name is confirmed against
  a real core, and so is the first Unix one (`$XDG_RUNTIME_DIR/herdr-core/<session>.sock`: the probe
  passes end to end on Debian 13); the fallbacks and macOS are not yet. The
  Unix-socket transport itself (connect, handshake, framed calls, "unavailable" when nothing
  listens) is also tested over a real AF_UNIX socket, which Windows 10 and later have
  (`UnixSocketTransportTests`).
- **Disposing while reading.** Disposing a `TerminalStream` or `EventSubscription` while another
  thread is reading it is safe: the read in hand stays valid until that reader's `AdvanceTo`, and
  the next read reports the end.
- **Credit after close.** Credit is returned in 16 KiB batches, or as soon as the reader drains what
  it read; none is sent after a channel has closed.
- **Resize notifications.** With `TerminalAttachOptions.ResizeNotifications`, the core announces the
  terminal's grid (another viewer resized it) with a RESIZE frame and then a fresh paint. The stream
  raises `Resized` on the thread reading `Output`, inside its read, and cuts that read short so no
  byte after the RESIZE is returned before the event: apply the size, then feed the bytes.
- **Event payloads** (`HerdrEvent.AsStatus()`, `AsTurn()`, …) follow herdr-core's `schema/README.md`;
  the OpenRPC document does not describe them, so the contract tests cannot check them.

## Licence

MIT. The vendored OpenRPC documents in `api/` are herdr-core's, under Apache-2.0
([api/NOTICE.md](api/NOTICE.md)).
