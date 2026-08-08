using MailTester.Cli;
using MailTester.Errors;
using MailTester.Modes;
using MailTester.Output;
using MailTester.Tests.Fakes;
using MimeKit;

namespace MailTester.Tests.Modes;

public class SendRunnerTests
{
    static CliOptions Options(int port, string? user = null, string? password = null) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Security = SecurityMode.None,
        From = MailboxAddress.Parse("a@x.com"),
        To = [MailboxAddress.Parse("b@y.com")],
        User = user,
        Password = password,
        TimeoutSeconds = 5,
    };

    static async Task<(ExitCode Code, string Text)> RunAsync(CliOptions options)
    {
        var output = new StringWriter();
        using var log = new ConsoleLog(output, null, new NullColorizer(), () => TimeSpan.Zero);

        var code = await SendRunner.RunAsync(options, log, CancellationToken.None);
        return (code, output.ToString());
    }

    [Fact]
    public async Task A_successful_send_returns_zero_and_says_so()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());

        var (code, text) = await RunAsync(Options(server.Port, "bob@fake.local", "s3cr3t"));

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("ÉXITO", text);
        Assert.Contains("queued as", text);
    }

    [Fact]
    public async Task A_rejected_sender_returns_the_rejection_code_and_renders_the_explanation()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.RejectsSender());

        var (code, text) = await RunAsync(Options(server.Port));

        Assert.Equal(ExitCode.SmtpRejected, code);
        Assert.Contains("FALLA EN FASE: ENVÍO", text);
        Assert.Contains("Qué probar", text);
        Assert.Contains("exit code 6", text);
    }

    [Fact]
    public async Task Rejected_credentials_return_the_authentication_code()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.RejectsAuth());

        var (code, _) = await RunAsync(Options(server.Port, "bob@fake.local", "wrong"));

        Assert.Equal(ExitCode.AuthenticationFailure, code);
    }

    [Fact]
    public async Task The_message_id_is_reported_so_it_can_be_searched_in_server_logs()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());

        var (_, text) = await RunAsync(Options(server.Port));

        Assert.Contains("Message-Id", text);
    }
}
