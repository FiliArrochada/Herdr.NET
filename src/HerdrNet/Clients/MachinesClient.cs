using HerdrNet.Model;
using HerdrNet.Serialization;

namespace HerdrNet;

/// <summary>Machines: other cores this core reaches; their terminals are addressed <c>machine/t7</c>.</summary>
public sealed class MachinesClient
{
    readonly HerdrConnection _connection;

    internal MachinesClient(HerdrConnection connection) => _connection = connection;

    /// <summary>The machines this core has added, and their state.</summary>
    public Task<MachineListResult> ListAsync(CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.MachineList, new EmptyParams(), HerdrJsonContext.Default.EmptyParams, HerdrJsonContext.Default.MachineListResult, cancellationToken);

    /// <summary>
    /// Asks this core to dial an added machine again now, and returns its line as it stands after the
    /// attempt (the core's own retry policy and what dialling does on the other side are not documented).
    /// </summary>
    public Task<MachineView> ConnectAsync(string machine, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.MachineConnect, new MachineParams(machine), HerdrJsonContext.Default.MachineParams, HerdrJsonContext.Default.MachineView, cancellationToken);
}
