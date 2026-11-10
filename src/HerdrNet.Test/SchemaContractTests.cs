using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HerdrNet.Serialization;

namespace HerdrNet.Test;

/// <summary>
/// Locks the hand-written models to the vendored OpenRPC document (api/): every wire name the client
/// reads or writes exists in the schema, and every field the schema requires is modelled.
/// When a new spec is vendored, point <see cref="SchemaFile"/> at it and fix what fails.
/// </summary>
public class SchemaContractTests
{
    const string SchemaFile = "herdr-core-0.1.20.openrpc.json";

    static readonly Lazy<JsonElement> Schema = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "api", SchemaFile))).RootElement.Clone());

    static readonly HerdrJsonContext Json = HerdrJsonContext.Default;

    public static TheoryData<string, string> Results => new()
    {
        { "HelloResult", nameof(Json.HelloResult) },
        { "CoreInfo", "CoreInfo" },
        { "LoginSession", "LoginSession" },
        { "Terminal", nameof(Json.Terminal) },
        { "Status", "TerminalStatus" },
        { "Foreground", "ForegroundProcess" },
        { "RunResult", nameof(Json.RunResult) },
        { "ListResult", nameof(Json.ListResult) },
        { "ReadResult", nameof(Json.TerminalReadResult) },
        { "AttachResult", nameof(Json.AttachResult) },
        { "SubscribeResult", nameof(Json.SubscribeResult) },
        { "StatusResult", nameof(Json.ServerStatus) },
        { "DocGetResult", nameof(Json.DocGetResult) },
        { "DocSetResult", nameof(Json.DocSetResult) },
        { "Display", nameof(Json.Display) },
        { "RemoveResult", nameof(Json.RemoveResult) },
        { "SendResult", nameof(Json.SendResult) },
        { "WaitResult", nameof(Json.WaitResult) },
        { "KeysResult", nameof(Json.KeysResult) },
        { "InterruptResult", nameof(Json.InterruptResult) },
        { "TemplatesResult", nameof(Json.TemplatesResult) },
        { "TemplateInfo", "TemplateInfo" },
        { "MachineListResult", nameof(Json.MachineListResult) },
        { "MachineView", "MachineView" },
    };

    [Theory]
    [MemberData(nameof(Results))]
    public void Result_models_match_their_schema(string schemaName, string typeName)
    {
        var schema = Schema.Value.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
        var wire = Names(TypeInfo(typeName));
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        var required = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];

        Assert.Empty(wire.Except(properties));     // nothing invented
        Assert.Empty(required.Except(wire));        // nothing required left out
    }

    public static TheoryData<string, string> Params => new()
    {
        { "hello", nameof(Json.HelloParams) },
        { "server.status", nameof(Json.EmptyParams) },
        { "terminal.list", nameof(Json.ListParams) },
        { "terminal.get", nameof(Json.TerminalParams) },
        { "terminal.kill", nameof(Json.TerminalParams) },
        { "terminal.read", nameof(Json.ReadParams) },
        { "terminal.label", nameof(Json.LabelParams) },
        { "terminal.attach", nameof(Json.AttachParams) },
        { "terminal.run", nameof(Json.TerminalRunOptions) },
        { "events.subscribe", nameof(Json.SubscribeParams) },
        { "doc.get", nameof(Json.DocGetParams) },
        { "doc.set", nameof(Json.DocSetParams) },
        { "terminal.rm", nameof(Json.TerminalParams) },
        { "terminal.keep", nameof(Json.KeepParams) },
        { "terminal.send", nameof(Json.SendParams) },
        { "terminal.keys", nameof(Json.KeysParams) },
        { "terminal.interrupt", nameof(Json.InterruptParams) },
        { "terminal.wait", nameof(Json.WaitParams) },
        { "templates.list", nameof(Json.EmptyParams) },
        { "machine.list", nameof(Json.EmptyParams) },
    };

    [Theory]
    [MemberData(nameof(Params))]
    public void Request_params_match_their_method(string method, string typeName)
    {
        var parameters = Method(method).GetProperty("params").EnumerateArray().ToArray();
        var names = parameters.Select(p => p.GetProperty("name").GetString()!).ToHashSet();
        var required = parameters.Where(p => p.TryGetProperty("required", out var r) && r.GetBoolean()).Select(p => p.GetProperty("name").GetString()!);
        var wire = Names(TypeInfo(typeName));

        Assert.Empty(wire.Except(names));
        Assert.Empty(required.Except(wire));
    }

    [Fact]
    public void Every_method_the_client_calls_exists()
    {
        foreach (var method in HerdrMethods.All)
            Method(method);
    }

    static JsonElement Method(string name) =>
        Schema.Value.GetProperty("methods").EnumerateArray().SingleOrDefault(m => m.GetProperty("name").GetString() == name) is { ValueKind: JsonValueKind.Object } found
            ? found
            : throw new Xunit.Sdk.XunitException($"{SchemaFile} has no method {name}.");

    static JsonTypeInfo TypeInfo(string name) =>
        (JsonTypeInfo?)typeof(HerdrJsonContext).GetProperty(name)!.GetValue(Json)
        ?? throw new InvalidOperationException(name);

    // Wire members only: computed helpers (no setter, no constructor parameter) never reach the wire.
    static HashSet<string> Names(JsonTypeInfo info) =>
        info.Properties.Where(p => p.Set is not null || p.AssociatedParameter is not null).Select(p => p.Name).ToHashSet();
}
