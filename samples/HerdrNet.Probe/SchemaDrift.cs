using System.Text.Json;

/// <summary>
/// Compares the OpenRPC document a live core returns from <c>rpc.discover</c> with the vendored one in
/// <c>api/</c> for the same version (or the newest vendored one when that version is not vendored yet).
/// The contract test checks the models against the vendored document; this checks the vendored
/// document against what a real core says, so the two together tie the models to the core.
/// </summary>
static class SchemaDrift
{
    /// <summary>The vendored document for <paramref name="version"/>, or the newest one; null when api/ is not found.</summary>
    public static (string Path, bool SameVersion)? FindVendored(string version)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var api = Path.Combine(dir.FullName, "api");
            if (!Directory.Exists(api))
                continue;
            var exact = Path.Combine(api, $"herdr-core-{version}.openrpc.json");
            if (File.Exists(exact))
                return (exact, true);
            var newest = Directory.GetFiles(api, "herdr-core-*.openrpc.json")
                .OrderBy(f => Version.TryParse(Path.GetFileName(f)["herdr-core-".Length..^".openrpc.json".Length], out var v) ? v : new Version())
                .LastOrDefault();
            if (newest is not null)
                return (newest, false);
        }

        return null;
    }

    /// <summary>Every difference between the two documents, one line each; empty when they agree.</summary>
    public static List<string> Compare(JsonElement vendored, JsonElement live)
    {
        var diffs = new List<string>();

        var vMethods = Methods(vendored);
        var lMethods = Methods(live);
        foreach (var name in lMethods.Keys.Except(vMethods.Keys).Order())
            diffs.Add($"method added: {name}");
        foreach (var name in vMethods.Keys.Except(lMethods.Keys).Order())
            diffs.Add($"method removed: {name}");
        foreach (var name in vMethods.Keys.Intersect(lMethods.Keys).Order())
        {
            var (vParams, vRequired) = Params(vMethods[name]);
            var (lParams, lRequired) = Params(lMethods[name]);
            Sets(diffs, $"{name} param", vParams, lParams);
            Sets(diffs, $"{name} required param", vRequired, lRequired);
            var vResult = Result(vMethods[name]);
            var lResult = Result(lMethods[name]);
            if (vResult != lResult)
                diffs.Add($"{name} result: {vResult} -> {lResult}");
        }

        var vSchemas = Schemas(vendored);
        var lSchemas = Schemas(live);
        foreach (var name in lSchemas.Keys.Except(vSchemas.Keys).Order())
            diffs.Add($"schema added: {name}");
        foreach (var name in vSchemas.Keys.Except(lSchemas.Keys).Order())
            diffs.Add($"schema removed: {name}");
        foreach (var name in vSchemas.Keys.Intersect(lSchemas.Keys).Order())
        {
            Sets(diffs, $"{name} property", Names(vSchemas[name], "properties"), Names(lSchemas[name], "properties"));
            Sets(diffs, $"{name} required", Strings(vSchemas[name], "required"), Strings(lSchemas[name], "required"));
            Sets(diffs, $"{name} enum value", Strings(vSchemas[name], "enum"), Strings(lSchemas[name], "enum"));
        }

        return diffs;
    }

    static void Sets(List<string> diffs, string what, HashSet<string> vendored, HashSet<string> live)
    {
        foreach (var n in live.Except(vendored).Order())
            diffs.Add($"{what} added: {n}");
        foreach (var n in vendored.Except(live).Order())
            diffs.Add($"{what} removed: {n}");
    }

    static Dictionary<string, JsonElement> Methods(JsonElement doc) =>
        doc.TryGetProperty("methods", out var m)
            ? m.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!)
            : [];

    static Dictionary<string, JsonElement> Schemas(JsonElement doc) =>
        doc.TryGetProperty("components", out var c) && c.TryGetProperty("schemas", out var s)
            ? s.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)
            : [];

    static (HashSet<string> All, HashSet<string> Required) Params(JsonElement method)
    {
        var all = new HashSet<string>();
        var required = new HashSet<string>();
        if (method.TryGetProperty("params", out var ps))
            foreach (var p in ps.EnumerateArray())
            {
                var name = p.GetProperty("name").GetString()!;
                all.Add(name);
                if (p.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True)
                    required.Add(name);
            }

        return (all, required);
    }

    static string Result(JsonElement method) =>
        method.TryGetProperty("result", out var r) && r.TryGetProperty("schema", out var s)
            ? s.TryGetProperty("$ref", out var reference) ? reference.GetString() ?? "" : s.GetRawText()
            : "";

    static HashSet<string> Names(JsonElement schema, string property) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject().Select(e => e.Name).ToHashSet()
            : [];

    static HashSet<string> Strings(JsonElement schema, string property) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToHashSet()
            : [];
}
