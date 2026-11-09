using HerdrNet.Serialization;

namespace HerdrNet;

/// <summary>Event subscriptions.</summary>
public sealed class EventsClient
{
    readonly HerdrConnection _connection;

    internal EventsClient(HerdrConnection connection) => _connection = connection;

    /// <summary>Subscribes to matching terminals' changes (all when no filter). Dispose to unsubscribe.</summary>
    public async Task<EventSubscription> SubscribeAsync(string? terminal = null, string? childrenOf = null, IReadOnlyDictionary<string, string>? labels = null, CancellationToken cancellationToken = default)
    {
        var json = HerdrJsonContext.Default;
        var (result, channel) = await _connection.OpenChannelAsync(HerdrMethods.EventsSubscribe, new SubscribeParams(terminal, childrenOf, labels), json.SubscribeParams, json.SubscribeResult, cancellationToken).ConfigureAwait(false);
        return new EventSubscription(channel, result);
    }
}
