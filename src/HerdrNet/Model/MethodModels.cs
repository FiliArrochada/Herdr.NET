using System.Text.Json;

namespace HerdrNet.Model;

// Hand-maintained from api/herdr-core-0.1.18.openrpc.json, as Models.cs; checked by the contract tests.

/// <summary><c>terminal.rm</c> reply.</summary>
public sealed record RemoveResult
{
    /// <summary>The address of the terminal removed.</summary>
    public string Removed { get; init; } = "";
}

/// <summary><c>terminal.send</c> reply.</summary>
public sealed record SendResult
{
    /// <summary>The turn when the text went in.</summary>
    public long Turn { get; init; }
    /// <summary>The program's status word when the text went in (null: it never said).</summary>
    public string? Status { get; init; }
    /// <summary>Whether it went <c>working</c> within a few seconds; null when it doesn't report status.</summary>
    public bool? Started { get; init; }
    /// <summary>With <see cref="TerminalSendOptions.Wait"/>: how that turn ended.</summary>
    public WaitResult? Wait { get; init; }
}

/// <summary><c>terminal.wait</c> reply (also inside <see cref="SendResult"/>).</summary>
public sealed record WaitResult
{
    /// <summary>The terminal waited on.</summary>
    public string Terminal { get; init; } = "";
    /// <summary>False on timeout: a check-in, the terminal is still running.</summary>
    public bool Ended { get; init; }
    /// <summary><c>status</c>, <c>exit</c>, <c>output</c>, <c>timeout</c> or <c>not_started</c> (see <see cref="WaitReasons"/>).</summary>
    public string Reason { get; init; } = "";
    /// <summary>The turn counter.</summary>
    public long Turn { get; init; }
    /// <summary>The exact word the program reported.</summary>
    public string? Status { get; init; }
    /// <summary>Exit code, when it exited.</summary>
    public int? ExitCode { get; init; }
    /// <summary>Signal, when one ended it.</summary>
    public int? Signal { get; init; }
    /// <summary>With a message: the sender's address; null when a person sent it.</summary>
    public string? From { get; init; }
    /// <summary>With <c>children</c>: one result per child reported on.</summary>
    public IReadOnlyList<WaitResult> Children { get; init; } = [];
}

/// <summary>Words for <see cref="WaitResult.Reason"/>. Not exhaustive.</summary>
public static class WaitReasons
{
#pragma warning disable CS1591 // wire values
    public const string Status = "status";
    public const string Exit = "exit";
    public const string Output = "output";
    public const string Timeout = "timeout";
    public const string NotStarted = "not_started";
#pragma warning restore CS1591
}

/// <summary><c>terminal.keys</c> reply.</summary>
public sealed record KeysResult
{
    /// <summary>The turn when the keys went in.</summary>
    public long Turn { get; init; }
    /// <summary>The program's status word then.</summary>
    public string? Status { get; init; }
}

/// <summary><c>terminal.interrupt</c> reply.</summary>
public sealed record InterruptResult
{
    /// <summary><c>stopped</c>, <c>exited</c>, <c>still_working</c> or <c>unknown</c>.</summary>
    public string Outcome { get; init; } = "";
    /// <summary>The key sent: <c>esc</c> for programs that report status, else <c>ctrl+c</c>.</summary>
    public string Key { get; init; } = "";
}

/// <summary><c>templates.list</c> reply.</summary>
public sealed record TemplatesResult
{
    /// <summary>Every template the core knows.</summary>
    public IReadOnlyList<TemplateInfo> Templates { get; init; } = [];
}

/// <summary>A named agent role; run it with its name as <c>argv[0]</c>.</summary>
public sealed record TemplateInfo
{
    /// <summary>The name.</summary>
    public string Name { get; init; } = "";
    /// <summary>What it is for.</summary>
    public string Description { get; init; } = "";
    /// <summary>The command it runs.</summary>
    public string Command { get; init; } = "";
    /// <summary>Set when the template file can't be used.</summary>
    public string? Error { get; init; }
}

/// <summary><c>machine.list</c> reply.</summary>
public sealed record MachineListResult
{
    /// <summary>Machines added to this core.</summary>
    public IReadOnlyList<MachineView> Machines { get; init; } = [];
}

/// <summary>Another core this core reaches.</summary>
public sealed record MachineView
{
    /// <summary>What this core calls it: <c>rohan</c> in <c>rohan/t7</c>.</summary>
    public string Name { get; init; } = "";
    /// <summary>How it is reached: an ssh target, or a command when <see cref="Via"/>.</summary>
    public string Target { get; init; } = "";
    /// <summary>The other core's permanent id.</summary>
    public string Id { get; init; } = "";
    /// <summary>What that core calls itself.</summary>
    public string Host { get; init; } = "";
    /// <summary>Its hc version when last seen.</summary>
    public string Version { get; init; } = "";
    /// <summary><c>connecting</c>, <c>connected</c>, <c>needs_login</c> or <c>unreachable</c>.</summary>
    public string State { get; init; } = "";
    /// <summary>The hc session there (null: its default).</summary>
    public string? Session { get; init; }
    /// <summary>Since when it is not connected, ms since the epoch.</summary>
    public long? SinceMs { get; init; }
    /// <summary>Short-lived (a sandbox for a task).</summary>
    public bool Temporary { get; init; }
    /// <summary><see cref="Target"/> is a command that runs a shell line there.</summary>
    public bool Via { get; init; }
    /// <summary>Why it is not connected.</summary>
    public string? Why { get; init; }
}

/// <summary>Options of <c>terminal.send</c> besides the terminal and text.</summary>
public sealed record TerminalSendOptions
{
    /// <summary>Type even if the program is blocked (it may answer its dialog).</summary>
    public bool Force { get; init; }
    /// <summary>Only type if the status is exactly this (<c>idle</c> or <c>working</c>).</summary>
    public string? If { get; init; }
    /// <summary>Then wait for the turn this send started.</summary>
    public bool Wait { get; init; }
    /// <summary>With <see cref="Wait"/>: when it checks in (default: the core's config, else 180 s).</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Who is sending, so a later bare wait from the same caller waits for this send's turn.</summary>
    public string? Caller { get; init; }
}

/// <summary>Options of <c>terminal.wait</c>.</summary>
public sealed record TerminalWaitOptions
{
    /// <summary>The terminal; with <see cref="Children"/>, whose children.</summary>
    public string? Terminal { get; init; }
    /// <summary>Wait on children instead: <c>all</c> or <c>any</c>.</summary>
    public string? Children { get; init; }
    /// <summary>For programs, not agents: end when output printed after the wait started contains this text.</summary>
    public string? Output { get; init; }
    /// <summary>When it checks in (default: the core's config, else 180 s).</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>See <see cref="TerminalSendOptions.Caller"/>.</summary>
    public string? Caller { get; init; }
}
