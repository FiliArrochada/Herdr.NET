using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HerdrNet.Framing;
using HerdrNet.Model;
using HerdrNet.Serialization;

namespace HerdrNet;

/// <summary>
/// A snapshot of matching terminals, then every change in order. Read it with <see cref="ReadAllAsync"/>;
/// a reader that falls behind gets a <see cref="HerdrMissed"/> with a fresh snapshot rather than the backlog.
/// <c>seq</c> restarts with each core process: after a reconnect, subscribe again.
/// </summary>
public sealed class EventSubscription : IAsyncDisposable
{
    readonly ChannelEndpoint _channel;
    int _reading;
    int _disposed;

    internal EventSubscription(ChannelEndpoint channel, SubscribeResult snapshot)
    {
        _channel = channel;
        Seq = snapshot.Seq;
        Terminals = snapshot.Terminals;
    }

    /// <summary>Events after this sequence number follow.</summary>
    public long Seq { get; }

    /// <summary>The matching terminals when the subscription started.</summary>
    public IReadOnlyList<Terminal> Terminals { get; }

    /// <summary>Completes when the subscription's channel ends.</summary>
    public Task Completion => _channel.Closed;

    /// <summary>Notifications in order, until the subscription or connection ends. One reader only.</summary>
    public async IAsyncEnumerable<HerdrNotification> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0)
            throw new InvalidOperationException("An event subscription has one reader.");

        var reader = _channel.Reader;
        var batch = new List<HerdrNotification>();
        while (true)
        {
            var read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = read.Buffer;
            while (FrameCodec.TryReadLine(ref buffer, out var line))
            {
                if (!line.IsEmpty)
                    batch.Add(Parse(line));
            }

            // Consuming returns credit to the core.
            reader.AdvanceTo(buffer.Start, buffer.End);
            foreach (var notification in batch)
                yield return notification;
            batch.Clear();

            if (read.IsCompleted)
                yield break;
        }
    }

    internal static HerdrNotification Parse(in ReadOnlySequence<byte> line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException e)
        {
            throw new HerdrProtocolException($"Malformed event line: {e.Message}", e);
        }

        using (document)
        {
            var root = document.RootElement;
            // A JSON-RPC notification; a bare event object is accepted too.
            var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "event";
            var parameters = root.TryGetProperty("params", out var p) ? p : root;
            try
            {
                switch (method)
                {
                    case "event":
                        var e = parameters.Deserialize(HerdrJsonContext.Default.EventParams)!;
                        return new HerdrEvent(e.Seq, e.Type, e.Terminal, e.Machine, e.Data.ValueKind == JsonValueKind.Undefined ? default : e.Data.Clone());
                    case "heartbeat":
                        return new HerdrHeartbeat(parameters.Deserialize(HerdrJsonContext.Default.SeqParams)!.Seq);
                    case "missed":
                        var missed = parameters.Deserialize(HerdrJsonContext.Default.MissedParams)!;
                        return new HerdrMissed(missed.Seq, missed.Terminals);
                    default:
                        return new HerdrUnknownNotification(method, parameters.Clone());
                }
            }
            catch (JsonException ex)
            {
                throw new HerdrProtocolException($"Event {method} has a shape this client does not understand: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Ends the subscription (CLOSE on its channel).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _channel.CloseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await _channel.Reader.CompleteAsync().ConfigureAwait(false);
    }
}
