using HerdrNet.Model;
using HerdrNet.Serialization;

namespace HerdrNet;

/// <summary>The core's own status. Stopping and restarting the core are deliberately not offered.</summary>
public sealed class ServerClient
{
    readonly HerdrConnection _connection;

    internal ServerClient(HerdrConnection connection) => _connection = connection;

    /// <summary>The core's identity, version, uptime and terminal counts.</summary>
    public Task<ServerStatus> StatusAsync(CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.ServerStatus, new EmptyParams(), HerdrJsonContext.Default.EmptyParams, HerdrJsonContext.Default.ServerStatus, cancellationToken);
}
