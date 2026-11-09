using System.Text.Json;
using HerdrNet.Model;
using HerdrNet.Serialization;

namespace HerdrNet;

/// <summary>Opaque JSON documents the core keeps, with optimistic versions.</summary>
public sealed class DocumentsClient
{
    static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();
    readonly HerdrConnection _connection;

    internal DocumentsClient(HerdrConnection connection) => _connection = connection;

    /// <summary>Reads a document; version 0 and a JSON null when never written.</summary>
    public Task<DocGetResult> GetAsync(string name, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.DocGet, new DocGetParams(name), HerdrJsonContext.Default.DocGetParams, HerdrJsonContext.Default.DocGetResult, cancellationToken);

    /// <summary>
    /// Writes a document if <paramref name="version"/> is still current; null deletes it. A stale write
    /// throws <see cref="HerdrRpcException"/> with kind <c>conflict</c>: read again, then retry.
    /// </summary>
    public Task<DocSetResult> SetAsync(string name, JsonElement? value, ulong version, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.DocSet, new DocSetParams(name, value ?? Null, version), HerdrJsonContext.Default.DocSetParams, HerdrJsonContext.Default.DocSetResult, cancellationToken);
}
