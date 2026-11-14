using System.Text.Json;
using System.Text.Json.Serialization;

namespace HerdrNet.Model;

// Hand-maintained from the newest document in api/. Property names map to the wire through
// the snake_case policy in HerdrJsonContext; the contract tests check them against the schema.
// Open sets of words (state, reason, status) stay strings: the core may add values.

/// <summary><c>hello</c> reply: protocol generation, core identity, methods and features.</summary>
public sealed record HelloResult
{
    /// <summary>Protocol generation the core speaks.</summary>
    public uint Generation { get; init; }
    /// <summary>The core's version.</summary>
    public string Version { get; init; } = "";
    /// <summary>Which core this is.</summary>
    public CoreInfo Core { get; init; } = new();
    /// <summary>Whether the connection switched to framed mode after this reply.</summary>
    public bool Framed { get; init; }
    /// <summary>Every method the core answers.</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];
    /// <summary>Optional protocol features (<c>automatic_input</c>, <c>resize_notifications</c>, ...).</summary>
    public IReadOnlyList<string> Features { get; init; } = [];
    /// <summary>The OS login the core inherited; null when the core does not say.</summary>
    public LoginSession? LoginSession { get; init; }
    /// <summary>
    /// True when systemd lingering is on, false when the core's programs stop at the user's last logout;
    /// null when unsupported, not applicable or unknown (and from cores before 0.1.21).
    /// </summary>
    public bool? KeepsRunningAfterLogout { get; init; }
    /// <summary>The account an administrator passes to <c>loginctl enable-linger</c>, when that applies.</summary>
    public string? LogoutLingerUser { get; init; }
}

/// <summary>The OS login a process inherited (rather than text restored from its environment).</summary>
public sealed record LoginSession
{
    /// <summary>The login session's id.</summary>
    public uint Id { get; init; }
    /// <summary>The user's numeric id.</summary>
    public uint User { get; init; }
    /// <summary>Whether it is a graphical login; null when unknown.</summary>
    public bool? Gui { get; init; }
}

/// <summary>A core's identity.</summary>
public sealed record CoreInfo
{
    /// <summary>Permanent random id, reset only when the core's state is wiped.</summary>
    public string Id { get; init; } = "";
    /// <summary>Display name, the hostname by default.</summary>
    public string Name { get; init; } = "";
}

/// <summary>One terminal's facts, live or finished.</summary>
public record Terminal
{
    /// <summary><c>t7</c>: this terminal forever on this core; <c>machine/t7</c> for another machine's.</summary>
    public string Address { get; init; } = "";
    /// <summary>Optional unique name among live terminals.</summary>
    public string? Name { get; init; }
    /// <summary>Free-form labels, never interpreted by the core.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    /// <summary>The program and its arguments.</summary>
    public IReadOnlyList<string> Argv { get; init; } = [];
    /// <summary>Start folder.</summary>
    public string Cwd { get; init; } = "";
    /// <summary><c>live</c> or <c>finished</c> (see <see cref="TerminalStates"/>).</summary>
    public string State { get; init; } = "";
    /// <summary>Start time, ms since the Unix epoch.</summary>
    public long CreatedMs { get; init; }
    /// <summary>End time, ms since the Unix epoch.</summary>
    public long? FinishedMs { get; init; }
    /// <summary><c>exact</c>, or <c>inexact</c> after a core crash (older history may be missing).</summary>
    public string Screen { get; init; } = "";
    /// <summary>Width in cells.</summary>
    public int Cols { get; init; }
    /// <summary>Height in cells.</summary>
    public int Rows { get; init; }
    /// <summary>The program's process id while it runs.</summary>
    public int? Pid { get; init; }
    /// <summary>Exit code once ended.</summary>
    public int? ExitCode { get; init; }
    /// <summary>Signal that ended it (never on Windows).</summary>
    public int? Signal { get; init; }
    /// <summary>Why it ended or is pending (<c>exited</c>, <c>killed</c>, <c>spawn_pending</c>, ...).</summary>
    public string? Reason { get; init; }
    /// <summary>Replace a lost program with a shell, in place.</summary>
    public bool Keep { get; init; }
    /// <summary>The terminal that started it.</summary>
    public string? Parent { get; init; }
    /// <summary>The program's own report (OSC 7501 root record); null if it never said, or after it exited.</summary>
    public TerminalStatus? Status { get; init; }
    /// <summary>When the status word last changed, ms since the epoch.</summary>
    public long? StatusMs { get; init; }
    /// <summary>Other OSC 7501 records by id.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Records { get; init; }
    /// <summary>Whether the program ever sent an OSC 7501 report.</summary>
    public bool Reported { get; init; }
    /// <summary>Goes up each time the status leaves <c>working</c>.</summary>
    public long Turn { get; init; }
    /// <summary>The terminal title.</summary>
    public string Title { get; init; } = "";
    /// <summary>What runs in it now, and where; null once finished.</summary>
    public ForegroundProcess? Foreground { get; init; }
    /// <summary>The template that started it.</summary>
    public string? Template { get; init; }
    /// <summary>TERM_PROGRAM and version the program believes it runs in.</summary>
    public string? Identity { get; init; }
    /// <summary>Its parent gets notes about it.</summary>
    public bool Notify { get; init; }
    /// <summary>Removed right after it exits.</summary>
    public bool Rm { get; init; }

    /// <summary>True while the terminal is live.</summary>
    [JsonIgnore]
    public bool IsLive => State == TerminalStates.Live;
}

/// <summary>Words for <see cref="Terminal.State"/>.</summary>
public static class TerminalStates
{
    /// <summary>Running, or kept and waiting for a new shell.</summary>
    public const string Live = "live";
    /// <summary>Ended.</summary>
    public const string Finished = "finished";
}

/// <summary>What a program reported about itself through OSC 7501, stored as reported.</summary>
public sealed record TerminalStatus
{
    /// <summary><c>idle</c>, <c>working</c>, <c>done</c>, <c>blocked</c>, <c>error</c>, or another word.</summary>
    public string State { get; init; } = "";
    /// <summary><c>permission</c>, <c>question</c> or <c>auth</c> (blocked only, when the program said).</summary>
    public string? Kind { get; init; }
    /// <summary>The program's message.</summary>
    [JsonPropertyName("msg")]
    public string? Message { get; init; }
    /// <summary>The program's title for the work.</summary>
    public string? Title { get; init; }
    /// <summary>0..100 (working or blocked, when the program said).</summary>
    public int? Progress { get; init; }
    /// <summary>The reporting application.</summary>
    public string? App { get; init; }
}

/// <summary>The foreground process group's leader and its folder.</summary>
public sealed record ForegroundProcess
{
    /// <summary>Process id.</summary>
    public int Pid { get; init; }
    /// <summary>Its name as the OS has it (<c>node</c>, <c>vim</c>, <c>sh</c>).</summary>
    public string Name { get; init; } = "";
    /// <summary>Its arguments.</summary>
    public IReadOnlyList<string> Argv { get; init; } = [];
    /// <summary>Its working directory now.</summary>
    public string Cwd { get; init; } = "";
}

/// <summary><c>terminal.run</c> reply: the new terminal, plus output when it was waited on.</summary>
public sealed record RunResult : Terminal
{
    /// <summary>Output, when the run waited.</summary>
    public string? Output { get; init; }
}

/// <summary><c>terminal.list</c> reply.</summary>
public sealed record ListResult
{
    /// <summary>This machine's terminals, then each connected machine's.</summary>
    public IReadOnlyList<Terminal> Terminals { get; init; } = [];
    /// <summary>Finished terminals left out.</summary>
    public int HiddenFinished { get; init; }
    /// <summary>Machines added to this core and their lines (not modelled yet).</summary>
    public IReadOnlyList<JsonElement> Machines { get; init; } = [];
}

/// <summary><c>terminal.read</c> reply.</summary>
public sealed record TerminalReadResult
{
    /// <summary>The text.</summary>
    public string Text { get; init; } = "";
    /// <summary>False when part of what was asked for may be missing.</summary>
    public bool Complete { get; init; }
    /// <summary><c>screen</c>, <c>history</c> or <c>stitched</c>.</summary>
    public string How { get; init; } = "";
    /// <summary>Why only the screen, when so.</summary>
    public string? Why { get; init; }
}

/// <summary><c>terminal.attach</c> reply.</summary>
public sealed record AttachResult
{
    /// <summary>The stream channel.</summary>
    public uint Channel { get; init; }
    /// <summary>The terminal attached to.</summary>
    public Terminal Terminal { get; init; } = new();
    /// <summary>The whole path sends RESIZE before replacement paints.</summary>
    public bool ResizeNotifications { get; init; }
    /// <summary>The whole path accepts AUTOMATIC_INPUT frames.</summary>
    public bool AutomaticInput { get; init; }
}

/// <summary><c>events.subscribe</c> reply.</summary>
public sealed record SubscribeResult
{
    /// <summary>Events after this sequence number follow.</summary>
    public long Seq { get; init; }
    /// <summary>Matching terminals now.</summary>
    public IReadOnlyList<Terminal> Terminals { get; init; } = [];
    /// <summary>Framed mode: the channel events arrive on.</summary>
    public uint? Channel { get; init; }
}

/// <summary><c>server.status</c> reply.</summary>
public sealed record ServerStatus
{
    /// <summary>Which core.</summary>
    public CoreInfo Core { get; init; } = new();
    /// <summary>Its version.</summary>
    public string Version { get; init; } = "";
    /// <summary>Its process id.</summary>
    public int Pid { get; init; }
    /// <summary>How long it has run.</summary>
    public long UptimeMs { get; init; }
    /// <summary>Live terminals.</summary>
    public int Live { get; init; }
    /// <summary>Finished terminals.</summary>
    public int Finished { get; init; }
    /// <summary>The package manager this hc came from, if any.</summary>
    public string? InstalledBy { get; init; }
    /// <summary>
    /// True when systemd lingering is on, false when the core's programs stop at the user's last logout;
    /// null when unsupported, not applicable or unknown (and from cores before 0.1.21). Checked on every call.
    /// </summary>
    public bool? KeepsRunningAfterLogout { get; init; }
    /// <summary>The account an administrator passes to <c>loginctl enable-linger</c>, when that applies.</summary>
    public string? LogoutLingerUser { get; init; }
    /// <summary>Every holder process (not modelled yet).</summary>
    public IReadOnlyList<JsonElement> Holders { get; init; } = [];
}

/// <summary><c>doc.get</c> reply.</summary>
public sealed record DocGetResult
{
    /// <summary>The document, or JSON null when never written or deleted.</summary>
    public JsonElement Value { get; init; }
    /// <summary>Zero when never written; kept across deletions.</summary>
    public ulong Version { get; init; }
}

/// <summary><c>doc.set</c> reply.</summary>
public sealed record DocSetResult
{
    /// <summary>The new version.</summary>
    public ulong Version { get; init; }
}

/// <summary>A viewer's colours and cell size, used by the core to answer display queries.</summary>
public sealed record Display
{
    /// <summary>Default foreground, <c>#rrggbb</c>.</summary>
    public string? Fg { get; init; }
    /// <summary>Default background, <c>#rrggbb</c>.</summary>
    public string? Bg { get; init; }
    /// <summary>Cursor colour, <c>#rrggbb</c>.</summary>
    public string? Cursor { get; init; }
    /// <summary>Cell size in pixels: <c>[width, height]</c>.</summary>
    public IReadOnlyList<int>? Cell { get; init; }
}

/// <summary>A terminal size in cells.</summary>
public sealed record InitialSize
{
    /// <summary>Columns, at least 1.</summary>
    public ushort Cols { get; init; }
    /// <summary>Rows, at least 1.</summary>
    public ushort Rows { get; init; }
}

/// <summary>Parameters of <c>terminal.run</c>. Unset values take the core's defaults.</summary>
public sealed record TerminalRunOptions
{
    /// <summary>Program and arguments, or a template name and its task. With neither this nor <see cref="Line"/>: the machine's shell.</summary>
    public IReadOnlyList<string>? Argv { get; init; }
    /// <summary>A shell line instead of <see cref="Argv"/>, run by the machine's shell.</summary>
    public string? Line { get; init; }
    /// <summary>Unique name among live terminals: 1-32 of <c>a-z 0-9 - _ .</c></summary>
    public string? Name { get; init; }
    /// <summary>Working directory (default: the core's).</summary>
    public string? Cwd { get; init; }
    /// <summary>Overrides the core's environment and terminal identity; leave TERM, TERM_PROGRAM and COLORTERM out to get the core's defaults.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    /// <summary>Labels to start with.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
    /// <summary>Replace a lost program with a shell in place.</summary>
    public bool? Keep { get; init; }
    /// <summary>Size before a viewer attaches.</summary>
    public InitialSize? Size { get; init; }
    /// <summary>Colours and cell size for display queries from the start.</summary>
    public Display? Display { get; init; }
    /// <summary>No parent terminal.</summary>
    public bool? NoParent { get; init; }
    /// <summary>Run on another machine added to this core.</summary>
    public string? On { get; init; }
}

/// <summary>Parameters of <c>terminal.attach</c> besides the terminal.</summary>
public sealed record TerminalAttachOptions
{
    /// <summary>The viewer's width in cells.</summary>
    public ushort Cols { get; init; } = 80;
    /// <summary>The viewer's height in cells.</summary>
    public ushort Rows { get; init; } = 24;
    /// <summary>Scrollback lines to paint first (main screen only).</summary>
    public int? History { get; init; }
    /// <summary>Watch only: never type or resize.</summary>
    public bool? Observe { get; init; }
    /// <summary>The viewer's colours and cell size.</summary>
    public Display? Display { get; init; }
    /// <summary>Back after a dropped line: sets the size only once active again.</summary>
    public bool? Resumed { get; init; }
    /// <summary>
    /// Ask the core to announce the terminal's grid (RESIZE, then a fresh paint) whenever it changes, for
    /// example because another viewer resized it. Confirmed in <see cref="AttachResult.ResizeNotifications"/>;
    /// the stream raises <see cref="TerminalStream.Resized"/> in order with the output.
    /// </summary>
    public bool ResizeNotifications { get; init; }
}
