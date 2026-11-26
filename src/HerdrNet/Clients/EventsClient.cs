using HerdrNet.Serialization;

namespace HerdrNet;

/// <summary>Event subscriptions.</summary>
public sealed class EventsClient
{
    readonly HerdrConnection _connection;

    internal EventsClient(HerdrConnection connection) => _connection = connection;

    /// <summary>
    /// Subscribes to matching terminals' changes (all when no filter). Dispose to unsubscribe.
    /// <c>docs</c> names the documents whose <c>doc</c> events to hear; a name ending in <c>*</c> is a
    /// prefix (0.1.22+). Naming narrows like every filter: documents alone hear only them, with empty
    /// terminal snapshots; add a terminal filter to hear both.
    /// </summary>
    public async Task<EventSubscription> SubscribeAsync(string? terminal = null, string? childrenOf = null, IReadOnlyDictionary<string, string>? labels = null,
        IReadOnlyList<string>? docs = null, CancellationToken cancellationToken = default)
    {
        var json = HerdrJsonContext.Default;
        var (result, channel) = await _connection.OpenChannelAsync(HerdrMethods.EventsSubscribe, new SubscribeParams(terminal, childrenOf, labels, docs), json.SubscribeParams, json.SubscribeResult, cancellationToken).ConfigureAwait(false);
        return new EventSubscription(channel, result);
    }
}
