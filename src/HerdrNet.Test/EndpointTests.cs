using HerdrNet.Transport;

namespace HerdrNet.Test;

public class EndpointTests
{
    static HerdrEnvironment Env(bool windows, Dictionary<string, string> vars, string[]? dirs = null, Dictionary<string, string>? files = null) => new()
    {
        IsWindows = windows,
        Get = name => vars.GetValueOrDefault(name),
        DirectoryExists = path => dirs?.Contains(path) == true,
        ReadFile = path => files?.GetValueOrDefault(path),
        GetUid = () => 1000,
    };

    [Fact]
    public void Windows_uses_a_per_user_per_session_pipe_with_only_ascii_alphanumerics_of_the_user()
    {
        var endpoint = HerdrEndpoint.Resolve(null, Env(true, new() { ["USERNAME"] = "Jo.Ana-Ç1", ["APPDATA"] = @"C:\Users\jo\AppData\Roaming" }));

        Assert.Equal(@"\\.\pipe\herdr-core-JoAna1-default.sock", endpoint.Address);
        Assert.Equal("herdr-core-JoAna1-default.sock", endpoint.PipeName);
        Assert.Equal(HerdrEndpointKind.NamedPipe, endpoint.Kind);
    }

    [Fact]
    public void Session_comes_from_the_argument_then_HC_SESSION()
    {
        var env = Env(true, new() { ["USERNAME"] = "jo", ["HC_SESSION"] = "scratch" });

        Assert.EndsWith("-scratch.sock", HerdrEndpoint.Resolve(null, env).Address);
        Assert.EndsWith("-probe.sock", HerdrEndpoint.Resolve("probe", env).Address);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("x:y")]
    public void Rejects_session_names_that_cannot_be_folders(string session) =>
        Assert.Throws<ArgumentException>(() => HerdrEndpoint.Resolve(session, Env(true, new() { ["USERNAME"] = "jo" })));

    [Fact]
    public void Unix_prefers_XDG_RUNTIME_DIR()
    {
        var endpoint = HerdrEndpoint.Resolve(null, Env(false, new() { ["HOME"] = "/home/jo", ["XDG_RUNTIME_DIR"] = "/tmp/rt" }));

        Assert.Equal("/tmp/rt/herdr-core/default.sock", endpoint.Address);
        Assert.Equal(HerdrEndpointKind.UnixSocket, endpoint.Kind);
    }

    [Fact]
    public void Unix_falls_back_to_run_user_when_it_exists()
    {
        var endpoint = HerdrEndpoint.Resolve(null, Env(false, new() { ["HOME"] = "/home/jo" }, dirs: ["/run/user/1000"]));

        Assert.Equal("/run/user/1000/herdr-core/default.sock", endpoint.Address);
    }

    [Fact]
    public void Unix_falls_back_to_the_session_state_dir()
    {
        Assert.Equal("/home/jo/.local/state/herdr-core/default/default.sock",
            HerdrEndpoint.Resolve(null, Env(false, new() { ["HOME"] = "/home/jo" })).Address);
        Assert.Equal("/st/herdr-core/s1/s1.sock",
            HerdrEndpoint.Resolve("s1", Env(false, new() { ["HOME"] = "/home/jo", ["XDG_STATE_HOME"] = "/st" })).Address);
    }

    [Fact]
    public void HC_SOCKET_wins_over_config_and_defaults()
    {
        var files = new Dictionary<string, string> { ["/home/jo/.config/herdr-core/config.toml"] = "socket = \"/from/config.sock\"\n" };

        Assert.Equal("/from/env.sock", HerdrEndpoint.Resolve(null, Env(false, new() { ["HOME"] = "/home/jo", ["HC_SOCKET"] = "/from/env.sock" }, files: files)).Address);
        Assert.Equal("/from/config.sock", HerdrEndpoint.Resolve(null, Env(false, new() { ["HOME"] = "/home/jo" }, files: files)).Address);
    }

    [Fact]
    public void Windows_config_lives_in_APPDATA()
    {
        var files = new Dictionary<string, string> { [@"C:\R\herdr-core\config.toml"] = @"socket = '\\.\pipe\custom'" };

        var endpoint = HerdrEndpoint.Resolve(null, Env(true, new() { ["APPDATA"] = @"C:\R", ["USERNAME"] = "jo" }, files: files));

        Assert.Equal(@"\\.\pipe\custom", endpoint.Address);
        Assert.Equal("custom", endpoint.PipeName);
    }

    [Theory]
    [InlineData("socket = \"/a b/c.sock\"", "/a b/c.sock")]
    [InlineData("shell = [\"nu\"]\nsocket = 'C:\\x\\y'", "C:\\x\\y")]
    [InlineData("socket = \"C:\\\\x\"", "C:\\x")]
    [InlineData("[machines]\nsocket = \"/not/top/level\"", null)]
    [InlineData("# socket = \"/commented\"", null)]
    [InlineData("", null)]
    public void Reads_the_top_level_socket_setting(string toml, string? expected) =>
        Assert.Equal(expected, HerdrEndpoint.ConfiguredSocket(toml));

    [Fact]
    public void A_commands_description_names_the_program_and_none_of_its_arguments()
    {
        // The description goes into logs and error messages; arguments can hold key paths or passwords.
        var ssh = new HcConnectTransport("/usr/bin/ssh", ["-i", "/home/me/.ssh/secret_key", "box", "hc", "connect"]);
        Assert.Equal("ssh (a command)", ssh.Description);
        Assert.DoesNotContain("secret", ssh.Description);
        Assert.Equal("/opt/hc connect", new HcConnectTransport("/opt/hc", "work").Description);
    }
}
