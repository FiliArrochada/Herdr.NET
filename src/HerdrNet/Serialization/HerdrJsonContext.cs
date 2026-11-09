using System.Text.Json;
using System.Text.Json.Serialization;
using HerdrNet.Model;

namespace HerdrNet.Serialization;

// Request parameter shapes. Internal: callers use the typed client methods.
internal sealed record EmptyParams;
internal sealed record HelloParams(uint Generation, bool Framed);
internal sealed record TerminalParams(string Terminal);
internal sealed record ListParams(bool? All, IReadOnlyDictionary<string, string>? Labels);
internal sealed record ReadParams(string Terminal, int? Lines);
internal sealed record LabelParams(string Terminal, IReadOnlyDictionary<string, string> Labels);
internal sealed record AttachParams(string Terminal, ushort Cols, ushort Rows, int? History, bool? Observe, Display? Display, bool? Resumed, bool? ResizeNotifications);
internal sealed record KeepParams(string Terminal, bool Keep);
internal sealed record SendParams(string Terminal, string Text, bool? Force, string? If, bool? Wait, double? Timeout, string? Caller);
internal sealed record KeysParams(string Terminal, IReadOnlyList<string> Keys);
internal sealed record InterruptParams(string Terminal, double? Timeout);
internal sealed record WaitParams(string? Terminal, string? Children, string? Output, double? Timeout, string? Caller);
internal sealed record SubscribeParams(string? Terminal, string? ChildrenOf, IReadOnlyDictionary<string, string>? Labels);
internal sealed record MachineParams(string Machine);
internal sealed record DocGetParams(string Name);
internal sealed record DocSetParams(string Name, JsonElement Value, ulong Version);

// Notification payloads.
internal sealed record EventParams(long Seq, string Type, string? Terminal, string? Machine, JsonElement Data);
internal sealed record SeqParams(long Seq);
internal sealed record MissedParams(long Seq, IReadOnlyList<Terminal> Terminals);
internal sealed record CloseReason(string? Reason, int? ExitCode, int? Signal);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    // Unknown fields are expected: the API only grows.
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(EmptyParams))]
[JsonSerializable(typeof(HelloParams))]
[JsonSerializable(typeof(TerminalParams))]
[JsonSerializable(typeof(ListParams))]
[JsonSerializable(typeof(ReadParams))]
[JsonSerializable(typeof(LabelParams))]
[JsonSerializable(typeof(AttachParams))]
[JsonSerializable(typeof(SubscribeParams))]
[JsonSerializable(typeof(KeepParams))]
[JsonSerializable(typeof(SendParams))]
[JsonSerializable(typeof(KeysParams))]
[JsonSerializable(typeof(InterruptParams))]
[JsonSerializable(typeof(WaitParams))]
[JsonSerializable(typeof(MachineParams))]
[JsonSerializable(typeof(MachineView))]
[JsonSerializable(typeof(DocGetParams))]
[JsonSerializable(typeof(DocSetParams))]
[JsonSerializable(typeof(TerminalRunOptions))]
[JsonSerializable(typeof(EventParams))]
[JsonSerializable(typeof(SeqParams))]
[JsonSerializable(typeof(MissedParams))]
[JsonSerializable(typeof(CloseReason))]
[JsonSerializable(typeof(HelloResult))]
[JsonSerializable(typeof(Terminal))]
[JsonSerializable(typeof(RunResult))]
[JsonSerializable(typeof(ListResult))]
[JsonSerializable(typeof(TerminalReadResult))]
[JsonSerializable(typeof(AttachResult))]
[JsonSerializable(typeof(SubscribeResult))]
[JsonSerializable(typeof(ServerStatus))]
[JsonSerializable(typeof(DocGetResult))]
[JsonSerializable(typeof(DocSetResult))]
[JsonSerializable(typeof(Display))]
[JsonSerializable(typeof(RemoveResult))]
[JsonSerializable(typeof(SendResult))]
[JsonSerializable(typeof(WaitResult))]
[JsonSerializable(typeof(KeysResult))]
[JsonSerializable(typeof(InterruptResult))]
[JsonSerializable(typeof(TemplatesResult))]
[JsonSerializable(typeof(MachineListResult))]
[JsonSerializable(typeof(TerminalEndedData))]
[JsonSerializable(typeof(KeepData))]
[JsonSerializable(typeof(StatusData))]
[JsonSerializable(typeof(TurnData))]
[JsonSerializable(typeof(TitleData))]
[JsonSerializable(typeof(LabelsData))]
[JsonSerializable(typeof(SizeData))]
[JsonSerializable(typeof(DocChangedData))]
[JsonSerializable(typeof(ForegroundProcess))]
internal sealed partial class HerdrJsonContext : JsonSerializerContext;
