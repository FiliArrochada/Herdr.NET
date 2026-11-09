using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HerdrNet.Transport;

/// <summary>How a core endpoint is reached.</summary>
public enum HerdrEndpointKind
{
    /// <summary>A Unix domain socket path.</summary>
    UnixSocket,
    /// <summary>A Windows named pipe (<c>\\.\pipe\...</c>).</summary>
    NamedPipe,
}

/// <summary>
/// Where a core listens. <see cref="Resolve(string?)"/> follows herdr-core's documented rules:
/// <c>HC_SOCKET</c>, else <c>socket</c> in its <c>config.toml</c>, else the per-session default
/// (Unix: the runtime dir, else the session's state dir; Windows: a per-user named pipe).
/// </summary>
public sealed record HerdrEndpoint(string Address, HerdrEndpointKind Kind)
{
    const string PipePrefix = @"\\.\pipe\";

    /// <summary>The name to give <see cref="System.IO.Pipes.NamedPipeClientStream"/>: the address without <c>\\.\pipe\</c>.</summary>
    public string PipeName => Address.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase) ? Address[PipePrefix.Length..] : Address;

    /// <summary>The default session's name.</summary>
    public const string DefaultSession = "default";

    /// <summary>
    /// The endpoint of <paramref name="session"/>'s core on this machine, for this account.
    /// A null session means <c>HC_SESSION</c>, else <c>default</c>.
    /// </summary>
    public static HerdrEndpoint Resolve(string? session = null) => Resolve(session, HerdrEnvironment.Current);

    internal static HerdrEndpoint Resolve(string? session, HerdrEnvironment env)
    {
        var name = SessionName(session ?? env.Get("HC_SESSION"));
        var windows = env.IsWindows;
        var kind = windows ? HerdrEndpointKind.NamedPipe : HerdrEndpointKind.UnixSocket;

        var configHome = env.NonEmpty("XDG_CONFIG_HOME") ?? (windows
            ? env.NonEmpty("APPDATA") ?? Join(windows, Profile(env), @"AppData\Roaming")
            : Join(windows, env.NonEmpty("HOME") ?? "/", ".config"));
        var chosen = env.NonEmpty("HC_SOCKET")
            ?? ConfiguredSocket(env.ReadFile(Join(windows, configHome, "herdr-core", "config.toml")));
        if (chosen is not null)
            return new HerdrEndpoint(chosen, kind);

        if (windows)
        {
            // Pipe names carry the account (ASCII letters and digits only) and the session.
            var user = new string((env.Get("USERNAME") ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
            return new HerdrEndpoint($@"{PipePrefix}herdr-core-{user}-{name}.sock", kind);
        }

        var stateHome = env.NonEmpty("XDG_STATE_HOME") ?? Join(false, env.NonEmpty("HOME") ?? "/", ".local/state");
        var state = Join(false, stateHome, "herdr-core", name);
        var runtime = env.NonEmpty("XDG_RUNTIME_DIR");
        if (runtime is null)
        {
            var perUser = $"/run/user/{env.GetUid()}";
            runtime = env.DirectoryExists(perUser) ? perUser : null;
        }

        var dir = runtime is null ? state : Join(false, runtime, "herdr-core");
        return new HerdrEndpoint(Join(false, dir, name + ".sock"), kind);
    }

    /// <summary>Validates a session name the way the core does; empty means <c>default</c>.</summary>
    internal static string SessionName(string? raw)
    {
        var name = string.IsNullOrWhiteSpace(raw) ? DefaultSession : raw.Trim();
        if (name is "." or ".." || name.Any(c => char.IsControl(c) || "/\\:*?\"<>|".Contains(c)))
            throw new ArgumentException($"Session {name} can't be a folder name: use letters, digits, - _ .", nameof(raw));
        return name;
    }

    static readonly Regex SocketSetting = new(
        """^\s*socket\s*=\s*(?:"(?<basic>(?:[^"\\]|\\.)*)"|'(?<literal>[^']*)')""",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>The top-level <c>socket</c> string of a herdr-core <c>config.toml</c>, if any.</summary>
    internal static string? ConfiguredSocket(string? toml)
    {
        if (toml is null)
            return null;
        foreach (Match match in SocketSetting.Matches(toml))
        {
            // Only a key before the first table header is top-level.
            var firstTable = Regex.Match(toml, @"^\s*\[", RegexOptions.Multiline);
            if (firstTable.Success && firstTable.Index < match.Index)
                return null;
            var value = match.Groups["literal"].Success ? match.Groups["literal"].Value : Unescape(match.Groups["basic"].Value);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        return null;
    }

    static string Unescape(string basic)
    {
        var sb = new StringBuilder(basic.Length);
        for (var i = 0; i < basic.Length; i++)
        {
            if (basic[i] == '\\' && i + 1 < basic.Length)
            {
                i++;
                sb.Append(basic[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var c => c });
            }
            else
            {
                sb.Append(basic[i]);
            }
        }

        return sb.ToString();
    }

    static string Profile(HerdrEnvironment env) => env.NonEmpty("USERPROFILE") ?? @"C:\";

    static string Join(bool windows, params string[] parts)
    {
        var separator = windows ? '\\' : '/';
        var sb = new StringBuilder(parts[0]);
        foreach (var part in parts.Skip(1))
        {
            if (sb.Length > 0 && sb[^1] != separator && sb[^1] != '/')
                sb.Append(separator);
            sb.Append(part);
        }

        return sb.ToString();
    }
}

/// <summary>What endpoint resolution reads from the machine; replaced in tests.</summary>
internal sealed partial class HerdrEnvironment
{
    public required bool IsWindows { get; init; }
    public required Func<string, string?> Get { get; init; }
    public required Func<string, bool> DirectoryExists { get; init; }
    public required Func<string, string?> ReadFile { get; init; }
    public required Func<uint> GetUid { get; init; }

    public string? NonEmpty(string name) => Get(name) is { Length: > 0 } value ? value : null;

    public static HerdrEnvironment Current { get; } = new()
    {
        IsWindows = OperatingSystem.IsWindows(),
        Get = Environment.GetEnvironmentVariable,
        DirectoryExists = Directory.Exists,
        ReadFile = path =>
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        },
        GetUid = () => OperatingSystem.IsWindows() ? 0 : NativeUid(),
    };

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint NativeUid();
}
