using MailTester.Cli;
using MailTester.Errors;
using MailTester.Modes;
using MailTester.Output;
using MailTester.Tests.Fakes;

namespace MailTester.Tests.Modes;

public class ProbeRunnerTests
{
    static CliOptions Options(int port, string? user = null, string? password = null) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        PortSpecified = true,
        Security = SecurityMode.None,
        SecuritySpecified = true,
        Probe = true,
        User = user,
        Password = password,
        TimeoutSeconds = 5,
    };

    static async Task<(ExitCode Code, string Text)> RunAsync(CliOptions options)
    {
        var output = new StringWriter();
        using var log = new ConsoleLog(output, null, new NullColorizer(), () => TimeSpan.Zero);

        var code = await ProbeRunner.RunAsync(options, log, CancellationToken.None);
        return (code, output.ToString());
    }

    [Fact]
    public async Task A_working_combination_yields_success_and_a_recommendation()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());

        var (code, text) = await RunAsync(Options(server.Port, "bob@fake.local", "s3cr3t"));

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("Recomendado", text);
        Assert.Contains("MATRIZ DE RESULTADOS", text);
    }

    [Fact]
    public async Task Probe_never_sends_a_message()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());

        await RunAsync(Options(server.Port));

        Assert.DoesNotContain(server.CommandsReceived, c => c.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(server.CommandsReceived, c => c.Equals("DATA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task When_no_combination_works_the_exit_code_comes_from_the_most_advanced_failure()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.RejectsAuth());

        var (code, text) = await RunAsync(Options(server.Port, "bob@fake.local", "wrong"));

        Assert.Equal(ExitCode.AuthenticationFailure, code);
        Assert.Contains("Ninguna combinación funcionó", text);
    }

    [Fact]
    public async Task Each_attempt_is_announced_with_its_own_banner()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());

        var (_, text) = await RunAsync(Options(server.Port));

        Assert.Contains("INTENTO 1/1", text);
    }
}
