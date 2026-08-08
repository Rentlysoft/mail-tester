using MailTester.Cli;
using MailTester.Modes;
using MailTester.Output;
using MimeKit;

namespace MailTester.Tests.Modes;

public class RunHeaderTests
{
    static string Render(CliOptions options, string? logFileWarning = null)
    {
        var output = new StringWriter();
        using var log = new ConsoleLog(output, null, new NullColorizer(), () => TimeSpan.Zero);

        RunHeader.Render(log, options, logFileWarning);
        return output.ToString();
    }

    static CliOptions Options() => new()
    {
        Host = "smtp.foo.com",
        Port = 587,
        Security = SecurityMode.StartTls,
        Auth = AuthMechanism.Plain,
        User = "a@x.com",
        Password = "s3cr3t7",
        From = MailboxAddress.Parse("a@x.com"),
        To = [MailboxAddress.Parse("b@y.com"), MailboxAddress.Parse("c@z.com")],
        TimeoutSeconds = 30,
    };

    [Fact]
    public void The_header_states_the_configuration_that_is_about_to_be_used()
    {
        var text = Render(Options());

        Assert.Contains("smtp.foo.com:587", text);
        Assert.Contains("starttls", text);
        Assert.Contains("plain", text);
        Assert.Contains("a@x.com", text);
        Assert.Contains("b@y.com, c@z.com", text);
        Assert.Contains("timeout=30s", text);
    }

    [Fact]
    public void The_password_is_never_printed_not_even_its_length()
    {
        var text = Render(Options());

        Assert.DoesNotContain("s3cr3t7", text);
        Assert.Contains("pass=***", text);
        Assert.DoesNotContain("chars)", text);
    }

    [Fact]
    public void Without_credentials_the_header_says_authentication_is_off()
    {
        var options = Options() with { User = null, Password = null };

        var text = Render(options);

        Assert.Contains("sin autenticación", text);
    }

    [Fact]
    public void The_version_line_names_the_tool_the_runtime_and_MailKit()
    {
        var text = Render(Options());

        Assert.Contains("mail-tester", text);
        Assert.Contains(".NET", text);
        Assert.Contains("MailKit", text);
    }

    [Fact]
    public void A_log_file_warning_is_surfaced_as_a_warning()
    {
        var text = Render(Options(), "No se pudo abrir --log-file 'x': acceso denegado.");

        Assert.Contains("WARN", text);
        Assert.Contains("acceso denegado", text);
    }
}
