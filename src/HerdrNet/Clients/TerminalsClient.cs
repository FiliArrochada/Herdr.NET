using HerdrNet.Model;
using HerdrNet.Serialization;

namespace HerdrNet;

/// <summary>Terminals: list, start, inspect, label, end, and attach to them.</summary>
public sealed class TerminalsClient
{
    readonly HerdrConnection _connection;
    static readonly HerdrJsonContext Json = HerdrJsonContext.Default;

    internal TerminalsClient(HerdrConnection connection) => _connection = connection;

    /// <summary>Live terminals (with finished ones when <paramref name="all"/>), optionally only those carrying all <paramref name="labels"/>.</summary>
    public Task<ListResult> ListAsync(bool all = false, IReadOnlyDictionary<string, string>? labels = null, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.TerminalList, new ListParams(all ? true : null, labels), Json.ListParams, Json.ListResult, cancellationToken);

    /// <summary>One terminal's facts: <c>t7</c>, a name, or <c>machine/t7</c>.</summary>
    public Task<Terminal> GetAsync(string terminal, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.TerminalGet, new TerminalParams(terminal), Json.TerminalParams, Json.Terminal, cancellationToken);

    /// <summary>Starts a program (or the machine's shell) in a new terminal and returns once it runs.</summary>
    public Task<RunResult> RunAsync(TerminalRunOptions options, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.TerminalRun, options, Json.TerminalRunOptions, Json.RunResult, cancellationToken);

    /// <summary>Ends the program (hangup, then terminate, then kill). Only ever on an explicit request.</summary>
    public Task<Terminal> KillAsync(string terminal, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.TerminalKill, new TerminalParams(terminal), Json.TerminalParams, Json.Terminal, cancellationToken);

    /// <summary>Sets labels; an empty value removes that label.</summary>
    public Task<Terminal> LabelAsync(string terminal, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.TerminalLabel, new LabelParams(terminal, labels), Json.LabelParams, Json.Terminal, cancellationToken);

    /// <summary>The screen plus <paramref name="lines"/> of history, as text.</summary>
    public Task<TerminalReadResult> ReadAsync(string terminal, int? lines = null, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.TerminalRead, new ReadParams(terminal, lines), Json.ReadParams, Json.TerminalReadResult, cancellationToken);

    /// <summary>Removes a finished terminal's record. Only ever on an explicit request.</summary>
    public Task<RemoveResult> RemoveAsync(string terminal, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.TerminalRm, new TerminalParams(terminal), Json.TerminalParams, Json.RemoveResult, cancellationToken);

    /// <summary>Keeps (or stops keeping) a live terminal after its program is lost: a shell replaces it in place.</summary>
    public Task<Terminal> KeepAsync(string terminal, bool keep, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.TerminalKeep, new KeepParams(terminal, keep), Json.KeepParams, Json.Terminal, cancellationToken);

    /// <summary>
    /// Pastes <paramref name="text"/> and presses Enter, without attaching, once nobody has typed for about 3 s.
    /// Refuses a blocked program unless <see cref="TerminalSendOptions.Force"/>.
    /// </summary>
    public Task<SendResult> SendAsync(string terminal, string text, TerminalSendOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new TerminalSendOptions();
        var parameters = new SendParams(terminal, text, options.Force ? true : null, options.If, options.Wait ? true : null, options.Timeout?.TotalSeconds, options.Caller);
        return _connection.CallAsync(HerdrMethods.TerminalSend, parameters, Json.SendParams, Json.SendResult, cancellationToken);
    }

    /// <summary>Presses named keys (<c>enter</c>, <c>esc</c>, <c>ctrl+c</c>, <c>f5</c>, <c>a</c>, ...), encoded by the core for the program's keyboard modes.</summary>
    public Task<KeysResult> KeysAsync(string terminal, IReadOnlyList<string> keys, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.TerminalKeys, new KeysParams(terminal, keys), Json.KeysParams, Json.KeysResult, cancellationToken);

    /// <summary>Presses the stop key (Esc for programs that report status, else Ctrl+C) and reports what it saw. Never escalates.</summary>
    public Task<InterruptResult> InterruptAsync(string terminal, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _connection.CallAsync(HerdrMethods.TerminalInterrupt, new InterruptParams(terminal, timeout?.TotalSeconds), Json.InterruptParams, Json.InterruptResult, cancellationToken);

    /// <summary>Waits until the program is not working (or exited, or was lost), the output appears, or the timeout checks in. Can take minutes.</summary>
    public Task<WaitResult> WaitAsync(TerminalWaitOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var parameters = new WaitParams(options.Terminal, options.Children, options.Output, options.Timeout?.TotalSeconds, options.Caller);
        return _connection.CallAsync(HerdrMethods.TerminalWait, parameters, Json.WaitParams, Json.WaitResult, cancellationToken);
    }

    /// <summary>Opens a live stream to a live terminal. Dispose the stream to detach.</summary>
    public async Task<TerminalStream> AttachAsync(string terminal, TerminalAttachOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new TerminalAttachOptions();
        var parameters = new AttachParams(terminal, options.Cols, options.Rows, options.History, options.Observe, options.Display, options.Resumed,
            options.ResizeNotifications ? true : null);
        var (result, channel) = await _connection.OpenChannelAsync(HerdrMethods.TerminalAttach, parameters, Json.AttachParams, Json.AttachResult, cancellationToken).ConfigureAwait(false);
        return new TerminalStream(channel, result);
    }
}
