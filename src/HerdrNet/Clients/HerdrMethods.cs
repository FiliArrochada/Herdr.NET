namespace HerdrNet;

/// <summary>Methods names this client calls, as the core spells them.</summary>
public static class HerdrMethods
{
#pragma warning disable CS1591 // wire values
    public const string Hello = "hello";
    public const string ServerStatus = "server.status";
    public const string TerminalRun = "terminal.run";
    public const string TerminalList = "terminal.list";
    public const string TerminalGet = "terminal.get";
    public const string TerminalKill = "terminal.kill";
    public const string TerminalLabel = "terminal.label";
    public const string TerminalRead = "terminal.read";
    public const string TerminalAttach = "terminal.attach";
    public const string TerminalRm = "terminal.rm";
    public const string TerminalKeep = "terminal.keep";
    public const string TerminalSend = "terminal.send";
    public const string TerminalKeys = "terminal.keys";
    public const string TerminalInterrupt = "terminal.interrupt";
    public const string TerminalWait = "terminal.wait";
    public const string TemplatesList = "templates.list";
    public const string MachineList = "machine.list";
    public const string MachineConnect = "machine.connect";
    public const string EventsSubscribe = "events.subscribe";
    public const string DocGet = "doc.get";
    public const string DocSet = "doc.set";
#pragma warning restore CS1591

    /// <summary>Every method this client models.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Hello, ServerStatus, TerminalRun, TerminalList, TerminalGet, TerminalKill, TerminalLabel, TerminalRead, TerminalAttach,
         TerminalRm, TerminalKeep, TerminalSend, TerminalKeys, TerminalInterrupt, TerminalWait, TemplatesList, MachineList,
         MachineConnect, EventsSubscribe, DocGet, DocSet];
}
