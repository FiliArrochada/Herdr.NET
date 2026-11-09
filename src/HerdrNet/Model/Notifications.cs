using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HerdrNet.Serialization;

namespace HerdrNet.Model;

/// <summary>Something that arrived on an event subscription.</summary>
public abstract record HerdrNotification(long Seq);

/// <summary>
/// A fact change: <c>created</c>, <c>lost</c>, <c>restarted</c>, <c>keep</c>, <c>finished</c>, <c>removed</c>,
/// <c>status</c>, <c>turn</c>, <c>title</c>, <c>labels</c>, <c>size</c>, <c>foreground</c>, <c>doc</c>, or a type
/// added later. <see cref="Data"/> is as the core sent it.
/// </summary>
public sealed record HerdrEvent(long Seq, string Type, string? Terminal, string? Machine, JsonElement Data) : HerdrNotification(Seq)
{
    /// <summary>The whole terminal: <c>created</c>, <c>restarted</c>.</summary>
    public Terminal AsTerminal() => Read(HerdrJsonContext.Default.Terminal, HerdrEventTypes.Created, HerdrEventTypes.Restarted)!;

    /// <summary>Why it ended: <c>lost</c>, <c>finished</c>.</summary>
    public TerminalEndedData AsEnded() => Read(HerdrJsonContext.Default.TerminalEndedData, HerdrEventTypes.Lost, HerdrEventTypes.Finished)!;

    /// <summary><c>keep</c>.</summary>
    public KeepData AsKeep() => Read(HerdrJsonContext.Default.KeepData, HerdrEventTypes.Keep)!;

    /// <summary><c>status</c>: the program's OSC 7501 report.</summary>
    public StatusData AsStatus() => Read(HerdrJsonContext.Default.StatusData, HerdrEventTypes.Status)!;

    /// <summary><c>turn</c>.</summary>
    public TurnData AsTurn() => Read(HerdrJsonContext.Default.TurnData, HerdrEventTypes.Turn)!;

    /// <summary><c>title</c>.</summary>
    public TitleData AsTitle() => Read(HerdrJsonContext.Default.TitleData, HerdrEventTypes.Title)!;

    /// <summary><c>labels</c>.</summary>
    public LabelsData AsLabels() => Read(HerdrJsonContext.Default.LabelsData, HerdrEventTypes.Labels)!;

    /// <summary><c>size</c>.</summary>
    public SizeData AsSize() => Read(HerdrJsonContext.Default.SizeData, HerdrEventTypes.Size)!;

    /// <summary><c>foreground</c>: what runs in it now; null when nothing does.</summary>
    public ForegroundProcess? AsForeground() => Read(HerdrJsonContext.Default.ForegroundProcess, HerdrEventTypes.Foreground);

    /// <summary><c>doc</c>: which document changed.</summary>
    public DocChangedData AsDoc() => Read(HerdrJsonContext.Default.DocChangedData, HerdrEventTypes.Doc)!;

    T? Read<T>(JsonTypeInfo<T> info, params ReadOnlySpan<string> types) where T : class
    {
        if (!types.Contains(Type))
            throw new InvalidOperationException($"A {Type} event does not carry {typeof(T).Name}.");
        if (Data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        try
        {
            return Data.Deserialize(info);
        }
        catch (JsonException e)
        {
            throw new HerdrProtocolException($"The {Type} event has a shape this client does not understand: {e.Message}", e);
        }
    }
}

/// <summary>Sent every 15 s with the current sequence when nothing else is queued.</summary>
public sealed record HerdrHeartbeat(long Seq) : HerdrNotification(Seq);

/// <summary>
/// The subscriber fell behind (or a remote machine reconnected): a fresh snapshot.
/// Drop later events whose <see cref="HerdrNotification.Seq"/> is not above this one, and re-read documents.
/// </summary>
public sealed record HerdrMissed(long Seq, IReadOnlyList<Terminal> Terminals) : HerdrNotification(Seq);

/// <summary>A notification method this client does not know.</summary>
public sealed record HerdrUnknownNotification(string Method, JsonElement Params) : HerdrNotification(-1);

/// <summary>Event type words. Not exhaustive.</summary>
public static class HerdrEventTypes
{
#pragma warning disable CS1591 // wire values
    public const string Created = "created";
    public const string Lost = "lost";
    public const string Restarted = "restarted";
    public const string Keep = "keep";
    public const string Finished = "finished";
    public const string Removed = "removed";
    public const string Status = "status";
    public const string Turn = "turn";
    public const string Title = "title";
    public const string Labels = "labels";
    public const string Size = "size";
    public const string Foreground = "foreground";
    public const string Doc = "doc";
#pragma warning restore CS1591
}
