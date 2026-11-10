namespace HerdrNet;

/// <summary>Base type of every exception the client throws for herdr-core reasons.</summary>
public class HerdrException : Exception
{
    /// <summary>Creates the exception.</summary>
    public HerdrException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// A JSON-RPC error answered by the core. <see cref="Kind"/> is the core's stable error kind
/// (<c>not_found</c>, <c>unavailable</c>, <c>conflict</c>, ...); see <see cref="HerdrErrorKinds"/>.
/// The set is open: compare against the constants, never switch exhaustively.
/// </summary>
public sealed class HerdrRpcException : HerdrException
{
    /// <summary>Creates the exception.</summary>
    public HerdrRpcException(string method, int code, string message, string? kind, string? hint)
        : base(hint is null ? $"{method}: {message}" : $"{method}: {message} ({hint})")
    {
        Method = method;
        Code = code;
        RpcMessage = message;
        Kind = kind;
        Hint = hint;
    }

    /// <summary>The method that failed.</summary>
    public string Method { get; }

    /// <summary>The JSON-RPC error code (the core uses -32000 for its own errors).</summary>
    public int Code { get; }

    /// <summary>The core's message, without the hint.</summary>
    public string RpcMessage { get; }

    /// <summary>The core's stable error kind, when it sent one.</summary>
    public string? Kind { get; }

    /// <summary>What to do about it, when the core said.</summary>
    public string? Hint { get; }
}

/// <summary>Error kinds the core documents. Not exhaustive.</summary>
public static class HerdrErrorKinds
{
#pragma warning disable CS1591 // names are self-describing wire values
    public const string NotFound = "not_found";
    public const string Unavailable = "unavailable";
    public const string Conflict = "conflict";
    public const string InvalidParams = "invalid_params";
    public const string NameTaken = "name_taken";
    public const string NeedsFramed = "needs_framed";
    public const string Incompatible = "incompatible";
    public const string UnsupportedGeneration = "unsupported_generation";
    public const string NeedsUpdate = "needs_update";
    public const string SpawnFailed = "spawn_failed";
    public const string Unreachable = "unreachable";
    public const string MachineUnknown = "machine_unknown";
    public const string Finished = "finished";
    public const string Blocked = "blocked";
    public const string Cancelled = "cancelled";
    public const string Internal = "internal";
#pragma warning restore CS1591
}

/// <summary>The connection could not be opened, or was lost.</summary>
public class HerdrConnectionException : HerdrException
{
    /// <summary>Creates the exception.</summary>
    public HerdrConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Nothing listens at the core's endpoint: the core is not running there.</summary>
public sealed class HerdrCoreUnavailableException : HerdrConnectionException
{
    /// <summary>Creates the exception.</summary>
    public HerdrCoreUnavailableException(string endpoint, Exception? inner = null)
        : base($"No herdr-core is listening at {endpoint}.", inner) => Endpoint = endpoint;

    /// <summary>Where the client looked.</summary>
    public string Endpoint { get; }
}

/// <summary>The core speaks a protocol generation or mode this client does not.</summary>
public sealed class HerdrIncompatibleException : HerdrException
{
    /// <summary>Creates the exception.</summary>
    public HerdrIncompatibleException(string message) : base(message) { }
}

/// <summary>The core sent something that breaks the protocol (malformed frame or JSON).</summary>
public sealed class HerdrProtocolException : HerdrException
{
    /// <summary>Creates the exception.</summary>
    public HerdrProtocolException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>No core listens and no <c>hc</c> was found to reach one.</summary>
public sealed class HcNotFoundException(string? configuredPath, Exception? directFailure)
    : HerdrException(configuredPath is null
        ? "Herdr Core (hc) was not found on PATH or in its installer's folder, and no core is running."
        : $"hc was not found at {configuredPath}, and no core is running.", directFailure)
{
    /// <summary>The configured path that did not exist, if one was configured.</summary>
    public string? ConfiguredPath { get; } = configuredPath;
}
