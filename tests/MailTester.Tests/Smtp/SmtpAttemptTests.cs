using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailTester.Cli;
using MailTester.Output;
using MailTester.Smtp;
using MailTester.Tests.Fakes;
using MimeKit;

namespace MailTester.Tests.Smtp;

public class SmtpAttemptTests
{
    static CliOptions Options(
        string host = "127.0.0.1",
        string? user = null,
        string? password = null,
        AuthMechanism auth = AuthMechanism.Auto,
        int timeoutSeconds = 5) => new()
    {
        Host = host,
        From = MailboxAddress.Parse("a@x.com"),
        To = [MailboxAddress.Parse("b@y.com")],
        User = user,
        Password = password,
        Auth = auth,
        TimeoutSeconds = timeoutSeconds,
        AllowInvalidCert = true,
    };

    static (SmtpAttempt Attempt, StringWriter Output) Build(CliOptions options)
    {
        var output = new StringWriter();
        var log = new ConsoleLog(output, null, new NullColorizer(), () => TimeSpan.Zero);
        return (new SmtpAttempt(options, log), output);
    }

    static int ClosedPort()
    {
        // Bind and release, so the port is almost certainly free and refuses connections.
        var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task A_successful_send_reports_every_fact_needed_to_trace_it()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        var (attempt, output) = Build(Options(user: "bob@fake.local", password: "s3cr3t"));

        var result = await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Exception);
        Assert.Null(result.FailedPhase);
        Assert.True(result.Authenticated);
        Assert.True(result.MessageSent);
        Assert.Contains("queued as", result.ServerResponse!);
        Assert.False(string.IsNullOrWhiteSpace(result.MessageId));
        Assert.NotEmpty(result.ResolvedAddresses);
        Assert.NotNull(result.LocalEndPoint);
        Assert.Contains("AUTH=LOGIN PLAIN", result.Capabilities);
        Assert.Equal(["LOGIN", "PLAIN"], result.AuthMechanismsOffered);
        Assert.Equal(AttemptPhase.Quit, result.LastPhase);
        Assert.True(result.Total > TimeSpan.Zero);

        var text = output.ToString();
        Assert.Contains("1/6", text);
        Assert.Contains("6/6", text);
        Assert.Contains("S: 220 fake.local", text);
    }

    [Fact]
    public async Task Without_credentials_authentication_is_skipped_and_said_to_be_skipped()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        var (attempt, output) = Build(Options());

        var result = await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Authenticated);
        Assert.Contains("omitido", output.ToString());
        Assert.DoesNotContain(server.CommandsReceived, c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Auth_none_skips_authentication_even_with_a_user()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        var (attempt, _) = Build(Options(user: "bob@fake.local", auth: AuthMechanism.None));

        var result = await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Authenticated);
        Assert.DoesNotContain(server.CommandsReceived, c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Probe_mode_authenticates_but_sends_nothing()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        var (attempt, _) = Build(Options(user: "bob@fake.local", password: "s3cr3t"));

        var result = await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Authenticated);
        Assert.False(result.MessageSent);
        Assert.Null(result.ServerResponse);
        Assert.DoesNotContain(server.CommandsReceived, c => c.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_forced_mechanism_is_the_only_one_offered_to_the_server()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        var (attempt, _) = Build(Options(user: "bob@fake.local", password: "s3cr3t", auth: AuthMechanism.Login));

        var result = await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: false, CancellationToken.None);

        Assert.True(result.Authenticated);
        Assert.Equal("LOGIN", result.AuthMechanismUsed);
        Assert.Contains(server.CommandsReceived, c => c.Equals("AUTH LOGIN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Forcing_a_mechanism_the_server_never_advertised_warns_and_still_tries()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        var (attempt, output) = Build(Options(user: "bob@fake.local", password: "s3cr3t", auth: AuthMechanism.CramMd5));

        await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: false, CancellationToken.None);

        Assert.Contains("no anunció CRAM-MD5", output.ToString());
        Assert.Contains(server.CommandsReceived, c => c.StartsWith("AUTH CRAM-MD5", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_refused_connection_fails_in_the_tcp_phase()
    {
        var (attempt, _) = Build(Options());

        var result = await attempt.RunAsync(ClosedPort(), SecurityMode.None, sendMessage: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AttemptPhase.TcpConnect, result.FailedPhase);
        Assert.IsType<SocketException>(result.Exception);
    }

    [Fact]
    public async Task An_unresolvable_host_fails_in_the_dns_phase()
    {
        var (attempt, _) = Build(Options(host: "no-such-host.invalid"));

        var result = await attempt.RunAsync(587, SecurityMode.None, sendMessage: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AttemptPhase.Dns, result.FailedPhase);
    }

    [Fact]
    public async Task A_silent_server_fails_with_a_timeout_and_not_a_cancellation()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Silent());
        var (attempt, _) = Build(Options(timeoutSeconds: 1));

        var result = await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.IsType<TimeoutException>(result.Exception);
        Assert.Equal(AttemptPhase.Greeting, result.FailedPhase);
    }

    [Fact]
    public async Task A_server_that_does_not_speak_smtp_fails_before_the_ehlo()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.NotSmtp());
        var (attempt, output) = Build(Options(timeoutSeconds: 3));

        var result = await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AttemptPhase.Greeting, result.FailedPhase);
        // The junk the server actually sent is visible, which is the whole point.
        Assert.Contains("HTTP/1.1 400", output.ToString());
    }

    [Fact]
    public async Task Requiring_starttls_against_a_server_that_does_not_offer_it_fails()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.WithoutStartTls());
        var (attempt, _) = Build(Options());

        var result = await attempt.RunAsync(server.Port, SecurityMode.StartTls, sendMessage: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.IsType<NotSupportedException>(result.Exception);
    }

    [Fact]
    public async Task Rejected_credentials_fail_in_the_authenticate_phase()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.RejectsAuth());
        var (attempt, output) = Build(Options(user: "bob@fake.local", password: "wrong"));

        var result = await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AttemptPhase.Authenticate, result.FailedPhase);
        Assert.IsType<AuthenticationException>(result.Exception);
        // The server's own words survive redaction.
        Assert.Contains("535 5.7.8", output.ToString());
    }

    [Fact]
    public async Task A_rejected_sender_fails_in_the_send_phase_with_the_status_code()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.RejectsSender());
        var (attempt, _) = Build(Options());

        var result = await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AttemptPhase.Send, result.FailedPhase);
        var ex = Assert.IsType<SmtpCommandException>(result.Exception);
        Assert.Equal(550, (int)ex.StatusCode);
    }

    [Fact]
    public async Task The_credential_never_appears_in_the_output()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        var (attempt, output) = Build(Options(user: "bob@fake.local", password: "s3cr3t-p4ss"));

        await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: true, CancellationToken.None);

        var text = output.ToString();
        Assert.DoesNotContain("s3cr3t-p4ss", text);
        Assert.DoesNotContain(Convert.ToBase64String("\0bob@fake.local\0s3cr3t-p4ss"u8.ToArray()), text);
        Assert.Contains("***REDACTED", text);
    }

    [Fact]
    public async Task Phase_timings_are_recorded_for_the_phases_that_ran()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        var (attempt, _) = Build(Options());

        var result = await attempt.RunAsync(server.Port, SecurityMode.None, sendMessage: true, CancellationToken.None);

        Assert.Contains(AttemptPhase.Dns, result.PhaseTimings.Keys);
        Assert.Contains(AttemptPhase.TcpConnect, result.PhaseTimings.Keys);
        Assert.All(result.PhaseTimings.Values, span => Assert.True(span >= TimeSpan.Zero));
    }
}
