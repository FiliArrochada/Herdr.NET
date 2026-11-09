using HerdrNet.Model;
using HerdrNet.Serialization;

namespace HerdrNet;

/// <summary>Templates: named agent roles to start with <see cref="TerminalsClient.RunAsync"/> (the name as <c>argv[0]</c>).</summary>
public sealed class TemplatesClient
{
    readonly HerdrConnection _connection;

    internal TemplatesClient(HerdrConnection connection) => _connection = connection;

    /// <summary>Every template, by name and description.</summary>
    public Task<TemplatesResult> ListAsync(CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.TemplatesList, new EmptyParams(), HerdrJsonContext.Default.EmptyParams, HerdrJsonContext.Default.TemplatesResult, cancellationToken);
}
