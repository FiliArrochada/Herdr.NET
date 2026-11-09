using System.Collections.Immutable;
using HerdrNet.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HerdrNet;

/// <summary>What changed in a <see cref="TerminalMirror"/>.</summary>
public enum MirrorChangeKind
{
    /// <summary>Rebuild everything from <see cref="TerminalMirror.Terminals"/>: the first snapshot, or a fresh one after <c>missed</c>. Re-read documents too.</summary>
    Reset,
    /// <summary>A terminal joined the view.</summary>
    Added,
    /// <summary>A terminal's facts changed; <see cref="MirrorChange.Event"/> says which.</summary>
    Updated,
    /// <summary>A terminal left the view (removed, or no longer matches the label filter).</summary>
    Removed,
    /// <summary>A document changed (unfiltered mirrors only); read it with <c>doc.get</c>.</summary>
    DocumentChanged,
}

/// <summary>One change to a <see cref="TerminalMirror"/>.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Address">The terminal, except for <see cref="MirrorChangeKind.Reset"/> and <see cref="MirrorChangeKind.DocumentChanged"/>.</param>
/// <param name="Terminal">The terminal as it is now; the last known record for <see cref="MirrorChangeKind.Removed"/>.</param>
/// <param name="Event">The event that caused it; null for <see cref="MirrorChangeKind.Reset"/>.</param>
public sealed record MirrorChange(MirrorChangeKind Kind, string? Address, Terminal? Terminal, HerdrEvent? Event);

/// <summary>
/// A live copy of the core's terminals: subscribes, applies every event in <c>seq</c> order as the core
/// documents it, and takes the fresh snapshot a <c>missed</c> carries. Attach <see cref="Changed"/>, then
/// call <see cref="StartAsync"/>. It lives as long as its subscription: after a reconnect, start a new one.
/// </summary>
public sealed class TerminalMirror : IAsyncDisposable
{
    readonly HerdrConnection _connection;
    readonly IReadOnlyDictionary<string, string>? _labels;
    readonly ILogger _logger;
    readonly CancellationTokenSource _stop = new();
    ImmutableDictionary<string, Terminal> _terminals = ImmutableDictionary<string, Terminal>.Empty;
    EventSubscription? _subscription;
    Task _pump = Task.CompletedTask;
    long _seq;
    int _started;
    int _disposed;

    /// <summary>A mirror of every terminal, or only those carrying all <paramref name="labels"/>.</summary>
    public TerminalMirror(HerdrConnection connection, IReadOnlyDictionary<string, string>? labels = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _labels = labels is { Count: > 0 } ? labels : null;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The terminals now, by address. An immutable snapshot: safe to enumerate on any thread.</summary>
    public IReadOnlyDictionary<string, Terminal> Terminals => Volatile.Read(ref _terminals);

    /// <summary>The sequence number of the last event applied.</summary>
    public long Seq => Interlocked.Read(ref _seq);

    /// <summary>
    /// Raised for every change, in order, on the mirror's reading task (never on a UI thread): marshal
    /// before touching UI. <see cref="Terminals"/> already includes the change when it is raised.
    /// </summary>
    public event Action<MirrorChange>? Changed;

    /// <summary>Completes when the subscription ends (connection closed or lost, or disposed); faults on a protocol error.</summary>
    public Task Completion => _pump;

    /// <summary>Subscribes, loads the snapshot (raising <see cref="MirrorChangeKind.Reset"/>) and starts applying events.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A mirror starts once; create another after a reconnect.");
        _subscription = await _connection.Events.SubscribeAsync(labels: _labels, cancellationToken: cancellationToken).ConfigureAwait(false);
        Load(_subscription.Seq, _subscription.Terminals);
        _pump = Task.Run(() => PumpAsync(_subscription, _stop.Token), CancellationToken.None);
    }

    async Task PumpAsync(EventSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notification in subscription.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                Apply(notification);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal void Apply(HerdrNotification notification)
    {
        switch (notification)
        {
            case HerdrMissed missed:
                Load(missed.Seq, missed.Terminals);
                break;
            case HerdrEvent e when e.Seq > Seq:
                Interlocked.Exchange(ref _seq, e.Seq);
                ApplyEvent(e);
                break;
        }
    }

    void Load(long seq, IReadOnlyList<Terminal> terminals)
    {
        Interlocked.Exchange(ref _seq, seq);
        var builder = ImmutableDictionary.CreateBuilder<string, Terminal>();
        foreach (var t in terminals)
            builder[t.Address] = t;
        Volatile.Write(ref _terminals, builder.ToImmutable());
        Raise(new MirrorChange(MirrorChangeKind.Reset, null, null, null));
    }

    void ApplyEvent(HerdrEvent e)
    {
        if (e.Type == HerdrEventTypes.Doc)
        {
            Raise(new MirrorChange(MirrorChangeKind.DocumentChanged, null, null, e));
            return;
        }

        if (e.Terminal is not { } address)
            return;
        var current = Terminals.GetValueOrDefault(address);

        switch (e.Type)
        {
            case HerdrEventTypes.Created:
            case HerdrEventTypes.Restarted:
                Put(e, address, e.AsTerminal(), added: current is null);
                return;
            case HerdrEventTypes.Removed:
                if (current is not null)
                    Remove(e, address, current);
                return;
            case HerdrEventTypes.Labels:
                var labels = e.AsLabels();
                if (current is null)
                {
                    if (labels.Record is { } record)
                        Put(e, address, record, added: true);
                    return;
                }

                if (_labels is not null && !Matches(labels.Labels))
                    Remove(e, address, current with { Labels = labels.Labels });
                else
                    Put(e, address, current with { Labels = labels.Labels }, added: false);
                return;
        }

        if (current is null)
            return; // not in this view

        Terminal? updated = e.Type switch
        {
            HerdrEventTypes.Lost => Ended(current, e.AsEnded()) with { Pid = null },
            HerdrEventTypes.Finished => Ended(current, e.AsEnded()) with { State = TerminalStates.Finished },
            HerdrEventTypes.Keep => current with { Keep = e.AsKeep().Keep },
            HerdrEventTypes.Status => e.AsStatus() is var s ? current with { Status = s.Status, Records = s.Records, Reported = s.Reported } : null,
            HerdrEventTypes.Turn => current with { Turn = e.AsTurn().Turn },
            HerdrEventTypes.Title => current with { Title = e.AsTitle().Title },
            HerdrEventTypes.Size => e.AsSize() is var z ? current with { Cols = z.Cols, Rows = z.Rows } : null,
            HerdrEventTypes.Foreground => current with { Foreground = e.AsForeground() },
            _ => null, // a type added after this client: nothing to apply
        };
        if (updated is not null)
            Put(e, address, updated, added: false);
    }

    static Terminal Ended(Terminal t, TerminalEndedData d) =>
        t with { Reason = d.Reason, ExitCode = d.ExitCode, Signal = d.Signal, FinishedMs = d.FinishedMs, Foreground = null };

    bool Matches(IReadOnlyDictionary<string, string> labels) =>
        _labels!.All(f => labels.TryGetValue(f.Key, out var v) && v == f.Value);

    void Put(HerdrEvent e, string address, Terminal terminal, bool added)
    {
        Volatile.Write(ref _terminals, _terminals.SetItem(address, terminal));
        Raise(new MirrorChange(added ? MirrorChangeKind.Added : MirrorChangeKind.Updated, address, terminal, e));
    }

    void Remove(HerdrEvent e, string address, Terminal last)
    {
        Volatile.Write(ref _terminals, _terminals.Remove(address));
        Raise(new MirrorChange(MirrorChangeKind.Removed, address, last, e));
    }

    void Raise(MirrorChange change)
    {
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception ex)
        {
            // A handler's bug must not stop the mirror.
            _logger.LogError(ex, "A TerminalMirror.Changed handler threw on {Kind} {Terminal}", change.Kind, change.Address);
        }
    }

    /// <summary>Ends the subscription. The terminals are untouched.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // Stop the reader first: disposing the subscription completes the pipe it reads.
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "The mirror's reader ended with an error during dispose");
        }

        if (_subscription is not null)
            await _subscription.DisposeAsync().ConfigureAwait(false);

        _stop.Dispose();
    }
}
