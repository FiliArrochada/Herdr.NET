// Protocol probe: exercises a real herdr-core end to end and prints what it measured.
//
//   dotnet run --project samples/HerdrNet.Probe -- [--session NAME] [--hc PATH] [--flood] [--keep] [--verbose]
//
// It talks to the core of session NAME (default: herdrnet-probe, a separate core from your own),
// starts its own terminals labelled herdr-net.probe=1, and kills only those at the end (unless --keep).
// It never stops or restarts a core.

using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using HerdrNet;
using HerdrNet.Model;
using HerdrNet.Transport;
using Microsoft.Extensions.Logging;

var session = Arg("--session") ?? "herdrnet-probe";
var hcArg = Arg("--hc");
var flood = args.Contains("--flood");
var keep = args.Contains("--keep");
using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
    .SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Warning));
var logger = loggerFactory.CreateLogger("probe");

var report = new List<(string Step, string Result)>();
var created = new List<string>();
var labels = new Dictionary<string, string> { ["herdr-net.probe"] = "1" };
var windows = OperatingSystem.IsWindows();

IHerdrTransport transport;
if (hcArg is not null)
{
    var hc = HcLocator.Find(hcArg == "auto" ? null : hcArg) ?? Fail($"No hc found ({hcArg}).");
    transport = new HcConnectTransport(hc, session, logger);
}
else
{
    transport = new EndpointTransport(HerdrEndpoint.Resolve(session));
}

Step("transport", transport.Description);

HerdrConnection connection;
try
{
    connection = await Timed("connect + hello (framed)", () => HerdrConnection.ConnectAsync(transport, logger: logger));
}
catch (HerdrCoreUnavailableException e)
{
    Console.Error.WriteLine($"""

        {e.Message}
        No core runs for session '{session}'. Either start one (the CLI starts a core when it needs one):
            {(windows ? $"$env:HC_SESSION='{session}'; hc ls" : $"HC_SESSION={session} hc ls")}
        or let `hc connect` try:   --hc auto
        """);
    return 2;
}

await using (connection)
{
    Step("core", $"{connection.Hello.Version} id={connection.Hello.Core.Id} name={connection.Hello.Core.Name} generation={connection.Hello.Generation}");
    Step("features", string.Join(", ", connection.Hello.Features));
    var missing = HerdrMethods.All.Where(m => !connection.Hello.Methods.Contains(m)).ToArray();
    Step("modelled methods missing from core", missing.Length == 0 ? "none" : string.Join(", ", missing));

    var status = await Timed("server.status", () => connection.Server.StatusAsync());
    Step("status", $"pid={status.Pid} live={status.Live} finished={status.Finished} installed_by={status.InstalledBy ?? "-"}");
    var list = await Timed("terminal.list", () => connection.Terminals.ListAsync());
    Step("terminals", $"{list.Terminals.Count} live, {list.HiddenFinished} finished hidden");

    // Events: subscribe first, count what arrives while the rest runs.
    var subscription = await Timed("events.subscribe", () => connection.Events.SubscribeAsync());
    var eventCounts = new Dictionary<string, int>();
    var eventPump = Task.Run(async () =>
    {
        try
        {
            await foreach (var n in subscription.ReadAllAsync())
            {
                var key = n switch { HerdrEvent e => e.Type, HerdrHeartbeat => "heartbeat", HerdrMissed => "missed", HerdrUnknownNotification u => "?" + u.Method, _ => "?" };
                lock (eventCounts) eventCounts[key] = eventCounts.GetValueOrDefault(key) + 1;
            }
        }
        catch (HerdrException e)
        {
            Step("event stream error", e.Message);
        }
    });

    // A shell terminal: type into it, detach, reattach, then do it again over a brand-new connection.
    var shell = await Timed("terminal.run (machine shell)", () => connection.Terminals.RunAsync(new TerminalRunOptions
    {
        Name = $"probe-{Environment.ProcessId}",
        Labels = labels,
        Env = new Dictionary<string, string> { ["TERM"] = "xterm-256color", ["COLORTERM"] = "truecolor" },
        Size = new InitialSize { Cols = 100, Rows = 30 },
        NoParent = true,
    }));
    created.Add(shell.Address);
    Step("shell", $"{shell.Address} argv=[{string.Join(' ', shell.Argv)}] identity={shell.Identity}");

    await using (var stream = await Timed("terminal.attach", () => connection.Terminals.AttachAsync(shell.Address, new TerminalAttachOptions { Cols = 100, Rows = 30, History = 50 })))
    {
        Step("attach negotiated", $"automatic_input={stream.Attach.AutomaticInput} resize_notifications={stream.Attach.ResizeNotifications}");
        var paint = await Collect(stream, TimeSpan.FromSeconds(2));
        Step("first paint", $"{paint.Length} bytes, win32-input-mode {(paint.Contains("\e[?9001h") ? "ON" : paint.Contains("\e[?9001l") ? "off" : "not announced")}: {Show(paint, 160)}");

        var typed = Stopwatch.StartNew();
        await stream.WriteInputAsync(Encoding.UTF8.GetBytes("echo probe-$((6*7))\r"));
        var echo = await CollectUntil(stream, "probe-", TimeSpan.FromSeconds(5));
        Step("input -> output", echo.Found ? $"echo seen after {typed.Elapsed.TotalMilliseconds:0} ms" : $"NOT seen in 5 s: {Show(echo.Text, 160)}");

        await stream.ResizeAsync(120, 40);
        await stream.FocusAsync(true);
        Step("resize/focus", "sent 120x40, focus");
        var detach = Stopwatch.StartNew();
        await stream.DisposeAsync();
        var closed = await stream.Completion;
        Step("detach", $"{closed.Reason} in {detach.Elapsed.TotalMilliseconds:0} ms (terminal may still run: {closed.TerminalMayStillRun})");
    }

    var again = await connection.Terminals.GetAsync(shell.Address);
    Step("after detach", $"state={again.State} cols={again.Cols} rows={again.Rows}");

    await using (var reattached = await Timed("reattach (same connection)", () => connection.Terminals.AttachAsync(shell.Address, new TerminalAttachOptions { Cols = 120, Rows = 40 })))
    {
        var paint = await Collect(reattached, TimeSpan.FromSeconds(1));
        Step("reattach paint", $"{paint.Length} bytes, still shows probe-42: {paint.Contains("probe-42")}");
    }

    // The mandatory scenario in miniature: the client goes away entirely, a new one finds the terminal.
    await subscription.DisposeAsync();
    await eventPump;
    await connection.DisposeAsync();
    Step("client closed", "connection disposed");

    await using var second = await Timed("reconnect (new connection)", () => HerdrConnection.ConnectAsync(RecreateTransport(), logger: logger));
    var survivor = (await second.Terminals.ListAsync()).Terminals.FirstOrDefault(t => t.Address == shell.Address);
    Step("terminal survived client restart", survivor is { IsLive: true } ? "yes" : $"NO ({survivor?.State ?? "gone"})");
    await using (var third = await Timed("attach after reconnect", () => second.Terminals.AttachAsync(shell.Address, new TerminalAttachOptions { Cols = 120, Rows = 40, Resumed = true })))
    {
        var paint = await Collect(third, TimeSpan.FromSeconds(1));
        Step("paint after reconnect", $"{paint.Length} bytes, shows earlier output (probe-42): {paint.Contains("probe-42")}");
    }

    // Reattach latency: attach -> first paint bytes in hand, 20 times (the desktop's tab-switch cost minus rendering).
    var samples = new List<double>();
    for (var i = 0; i < 20; i++)
    {
        var watch = Stopwatch.StartNew();
        await using var s = await second.Terminals.AttachAsync(shell.Address, new TerminalAttachOptions { Cols = 120, Rows = 40, History = 200 });
        var first = await s.Output.ReadAsync();
        samples.Add(watch.Elapsed.TotalMilliseconds);
        s.Output.AdvanceTo(first.Buffer.End);
    }

    samples.Sort();
    Step("reattach to first paint (20 runs)", $"p50 {samples[10]:0.0} ms, p90 {samples[18]:0.0} ms, max {samples[^1]:0.0} ms");

    // The mirror the desktop lists from, filtered to this probe's terminals.
    await using (var mirror = new TerminalMirror(second, labels))
    {
        var changes = new List<MirrorChange>();
        mirror.Changed += c => { lock (changes) changes.Add(c); };
        await Timed("mirror start (labels filter)", async () => { await mirror.StartAsync(); return mirror.Seq; });
        Step("mirror snapshot", string.Join(", ", mirror.Terminals.Keys));

        var templates = await Timed("templates.list", () => second.Templates.ListAsync());
        Step("templates", templates.Templates.Count == 0 ? "none" : string.Join(", ", templates.Templates.Select(t => t.Error is null ? t.Name : $"{t.Name} (unusable)")));
        var machines = await Timed("machine.list", () => second.Machines.ListAsync());
        Step("machines", machines.Machines.Count == 0 ? "none" : string.Join(", ", machines.Machines.Select(m => $"{m.Name}={m.State}")));

        var sent = await Timed("terminal.send (waits ~3 s of quiet)", () => second.Terminals.SendAsync(shell.Address, "echo sent-$((6*7))"));
        Step("send result", $"turn={sent.Turn} status={sent.Status ?? "none"} started={sent.Started?.ToString() ?? "unknown"}");
        await Task.Delay(1000);
        var text = await second.Terminals.ReadAsync(shell.Address, lines: 20);
        Step("send reached the shell", text.Text.Contains("sent-42") ? "yes" : "NO");

        // resize_notifications: an observer that opted in hears another viewer's size, in order with output.
        await using (var observer = await second.Terminals.AttachAsync(shell.Address, new TerminalAttachOptions { Cols = 120, Rows = 40, Observe = true, ResizeNotifications = true }))
        {
            var grids = new List<string>();
            observer.Resized += (c, r) => { lock (grids) grids.Add($"{c}x{r}"); };
            await Collect(observer, TimeSpan.FromMilliseconds(500));
            await using (var active = await second.Terminals.AttachAsync(shell.Address, new TerminalAttachOptions { Cols = 90, Rows = 28 }))
            {
                await Collect(active, TimeSpan.FromMilliseconds(300));
                await active.ResizeAsync(96, 30);
                await Collect(observer, TimeSpan.FromMilliseconds(800));
            }

            lock (grids)
                Step("resize_notifications", $"confirmed={observer.Attach.ResizeNotifications}, observer saw grids: {(grids.Count == 0 ? "none" : string.Join(" -> ", grids))}");
        }

        lock (changes)
            Step("mirror changes", string.Join(", ", changes.GroupBy(c => c.Event?.Type ?? c.Kind.ToString()).Select(g => $"{g.Key}={g.Count()}")));
        var seen = mirror.Terminals.GetValueOrDefault(shell.Address);
        var fresh = await second.Terminals.GetAsync(shell.Address);
        Step("mirror matches terminal.get", seen is not null && (seen.Cols, seen.Rows, seen.Title, seen.Turn) == (fresh.Cols, fresh.Rows, fresh.Title, fresh.Turn) ? "yes" : $"NO (mirror {seen?.Cols}x{seen?.Rows} '{seen?.Title}', core {fresh.Cols}x{fresh.Rows} '{fresh.Title}')");
    }

    // The supervisor the desktop runs: connect (with fallback), mirror, close.
    var supervisorOptions = new HerdrOptions
    {
        Session = session,
        HcPath = hcArg is null or "auto" ? null : hcArg,
        Transport = hcArg is null ? HerdrTransportPreference.Direct : HerdrTransportPreference.HcConnect,
        MirrorLabels = labels,
    };
    await using (var supervisor = new ConnectionSupervisor(supervisorOptions, loggerFactory))
    {
        var watch = Stopwatch.StartNew();
        supervisor.Start();
        await supervisor.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Step("supervisor connected", $"{watch.Elapsed.TotalMilliseconds:0.0} ms, mirror has {supervisor.Mirror?.Terminals.Count ?? 0} probe terminal(s)");
    }

    if (flood)
        await Flood(second);

    lock (eventCounts)
        Step("events seen", eventCounts.Count == 0 ? "none" : string.Join(", ", eventCounts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));

    if (!keep)
    {
        foreach (var address in created)
        {
            try { await second.Terminals.KillAsync(address); }
            catch (HerdrRpcException e) { Step($"kill {address}", e.Message); }
        }

        Step("cleanup", $"killed {string.Join(", ", created)}");
        foreach (var address in created)
        {
            try { await second.Terminals.RemoveAsync(address); }
            catch (HerdrRpcException e) { Step($"rm {address}", e.Message); }
        }

        Step("cleanup", $"removed {string.Join(", ", created)}");
    }
}

Console.WriteLine();
Console.WriteLine("| Step | Result |");
Console.WriteLine("|---|---|");
foreach (var (step, result) in report)
    Console.WriteLine($"| {step} | {result.Replace("|", "\\|")} |");
return 0;

// ---------------------------------------------------------------------------------------------

async Task Flood(HerdrConnection c)
{
    const int lines = 200_000;
    IReadOnlyList<string> argv = windows
        ? ["powershell", "-NoProfile", "-Command", $"Start-Sleep -Milliseconds 800; 1..{lines}"]
        : ["sh", "-c", $"sleep 0.8; seq 1 {lines}"];
    var run = await c.Terminals.RunAsync(new TerminalRunOptions { Argv = argv, Labels = labels, NoParent = true, Name = $"probe-flood-{Environment.ProcessId}" });
    created.Add(run.Address);
    await using var stream = await c.Terminals.AttachAsync(run.Address, new TerminalAttachOptions { Cols = 120, Rows = 40 });
    var watch = Stopwatch.StartNew();
    long bytes = 0;
    while (true)
    {
        var read = await stream.Output.ReadAsync();
        bytes += read.Buffer.Length;
        stream.Output.AdvanceTo(read.Buffer.End);
        if (read.IsCompleted)
            break;
    }

    var closed = await stream.Completion;
    var seconds = watch.Elapsed.TotalSeconds;
    Step("flood", $"{lines} lines: {bytes / 1024.0 / 1024.0:0.0} MiB in {seconds:0.00} s = {bytes / 1024.0 / 1024.0 / seconds:0.0} MiB/s, ended {closed.Reason} exit={closed.ExitCode}");
}

IHerdrTransport RecreateTransport() => transport switch
{
    HcConnectTransport => new HcConnectTransport(HcLocator.Find(hcArg == "auto" ? null : hcArg)!, session, logger),
    EndpointTransport e => new EndpointTransport(e.Endpoint),
    _ => throw new InvalidOperationException(),
};

static async Task<string> Collect(TerminalStream stream, TimeSpan quiet)
{
    var text = new StringBuilder();
    while (true)
    {
        using var cts = new CancellationTokenSource(quiet);
        ReadResult read;
        try { read = await stream.Output.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { return text.ToString(); }
        text.Append(Encoding.UTF8.GetString(read.Buffer.ToArray()));
        stream.Output.AdvanceTo(read.Buffer.End);
        if (read.IsCompleted)
            return text.ToString();
    }
}

static async Task<(bool Found, string Text)> CollectUntil(TerminalStream stream, string marker, TimeSpan timeout)
{
    var text = new StringBuilder();
    using var cts = new CancellationTokenSource(timeout);
    try
    {
        // The echoed command line also contains the marker; wait for a second occurrence (the output).
        while (CountOf(text.ToString(), marker + "42") < 1)
        {
            var read = await stream.Output.ReadAsync(cts.Token);
            text.Append(Encoding.UTF8.GetString(read.Buffer.ToArray()));
            stream.Output.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                break;
        }
    }
    catch (OperationCanceledException)
    {
    }

    return (CountOf(text.ToString(), marker + "42") >= 1, text.ToString());
}

static int CountOf(string text, string value)
{
    var count = 0;
    for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        count++;
    return count;
}

static string Show(string text, int max)
{
    var sb = new StringBuilder();
    foreach (var c in text)
    {
        if (sb.Length >= max) { sb.Append('…'); break; }
        sb.Append(c switch { '\e' => "␛", '\r' => "␍", '\n' => "␊", _ when char.IsControl(c) => $"\\x{(int)c:x2}", _ => c.ToString() });
    }

    return sb.ToString();
}

async Task<T> Timed<T>(string step, Func<Task<T>> action)
{
    var watch = Stopwatch.StartNew();
    try
    {
        var result = await action();
        Step(step, $"{watch.Elapsed.TotalMilliseconds:0.0} ms");
        return result;
    }
    catch (HerdrException e)
    {
        Step(step, $"FAILED after {watch.Elapsed.TotalMilliseconds:0} ms: {e.GetType().Name}: {e.Message}");
        throw;
    }
}

void Step(string step, string result)
{
    lock (report) report.Add((step, result));
    Console.WriteLine($"{step,-36} {result}");
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(2);
    return "";
}
