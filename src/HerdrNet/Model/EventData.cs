using System.Text.Json;

namespace HerdrNet.Model;

// Event payloads, from herdr-core's schema/README.md ("Events"): the OpenRPC document does not
// describe them, so the contract tests cannot check these. Read them with HerdrEvent.As*().

/// <summary><c>lost</c> and <c>finished</c> event data.</summary>
public sealed record TerminalEndedData
{
    /// <summary>Why (<c>exited</c>, <c>killed</c>, <c>signaled</c>, ...).</summary>
    public string? Reason { get; init; }
    /// <summary>Exit code, when it exited.</summary>
    public int? ExitCode { get; init; }
    /// <summary>Signal, when one ended it.</summary>
    public int? Signal { get; init; }
    /// <summary>End time; null for <c>lost</c> (a kept terminal stays live).</summary>
    public long? FinishedMs { get; init; }
}

/// <summary><c>keep</c> event data.</summary>
public sealed record KeepData
{
    /// <summary>Whether the terminal is now kept.</summary>
    public bool Keep { get; init; }
}

/// <summary><c>status</c> event data: replaces all three fields.</summary>
public sealed record StatusData
{
    /// <summary>The root status; null when cleared.</summary>
    public TerminalStatus? Status { get; init; }
    /// <summary>Every record by key.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Records { get; init; }
    /// <summary>Whether the program ever reported.</summary>
    public bool Reported { get; init; }
}

/// <summary><c>turn</c> event data.</summary>
public sealed record TurnData
{
    /// <summary>The turn counter after a turn ended.</summary>
    public long Turn { get; init; }
    /// <summary>The status word the turn ended on.</summary>
    public string? State { get; init; }
}

/// <summary><c>title</c> event data.</summary>
public sealed record TitleData
{
    /// <summary>The new title.</summary>
    public string Title { get; init; } = "";
}

/// <summary><c>labels</c> event data.</summary>
public sealed record LabelsData
{
    /// <summary>All labels now.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    /// <summary>The whole terminal, when it newly matches the subscription's label filter.</summary>
    public Terminal? Record { get; init; }
}

/// <summary><c>size</c> event data.</summary>
public sealed record SizeData
{
    /// <summary>Width in cells.</summary>
    public int Cols { get; init; }
    /// <summary>Height in cells.</summary>
    public int Rows { get; init; }
}

/// <summary><c>doc</c> event data: a document changed (read it with <c>doc.get</c>).</summary>
public sealed record DocChangedData
{
    /// <summary>The document's name.</summary>
    public string Name { get; init; } = "";
    /// <summary>Its new version.</summary>
    public ulong Version { get; init; }
}
