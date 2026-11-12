using HerdrNet.Transport;

namespace HerdrNet.Test;

/// <summary>A command transport's process gets the variables it was given, and loses the ones set to null.</summary>
public class CommandTransportTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task TheCommandSeesItsEnvironment()
    {
        // Set on this process, so only the transport's removal can hide it from the child.
        Environment.SetEnvironmentVariable("HERDRNET_TEST_GONE", "inherited");
        var environment = new Dictionary<string, string?>
        {
            ["HERDRNET_TEST_SET"] = "given",
            ["HERDRNET_TEST_GONE"] = null,
        };
        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/d", "/c", "echo [%HERDRNET_TEST_SET%][%HERDRNET_TEST_GONE%]" })
            : ("/bin/sh", new[] { "-c", "echo \"[$HERDRNET_TEST_SET][${HERDRNET_TEST_GONE:-%HERDRNET_TEST_GONE%}]\"" });

        var transport = new HcConnectTransport(program, arguments, session: null, logger: null, environment);
        await using var stream = await transport.OpenAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // cmd leaves an unset %VAR% as written; the shell prints the same text when it is unset.
        Assert.Equal("[given][%HERDRNET_TEST_GONE%]", line?.Trim());
    }
}
