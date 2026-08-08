using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace MailTester.Tests.Fakes;

public class FakeSmtpServerTests
{
    static MimeMessage Message()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("a@x.com"));
        message.To.Add(MailboxAddress.Parse("b@y.com"));
        message.Subject = "prueba";
        message.Body = new TextPart("plain") { Text = "cuerpo" };
        return message;
    }

    [Fact]
    public async Task A_real_MailKit_client_completes_a_full_dialogue_against_the_fake()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        using var client = new SmtpClient();

        await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("bob@fake.local", "s3cr3t");
        var response = await client.SendAsync(Message());
        await client.DisconnectAsync(true);

        Assert.Contains("queued as", response);
        Assert.Contains(server.CommandsReceived, c => c.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(server.CommandsReceived, c => c.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(server.CommandsReceived, c => c.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(server.CommandsReceived, c => c.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(server.CommandsReceived, c => c.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_message_body_reaches_the_fake_intact()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        using var client = new SmtpClient();

        await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None);
        await client.SendAsync(Message());
        await client.DisconnectAsync(true);

        Assert.NotNull(server.DataReceived);
        Assert.Contains("Subject: prueba", server.DataReceived!);
        Assert.Contains("cuerpo", server.DataReceived!);
    }

    [Fact]
    public async Task The_advertised_capabilities_are_the_ones_the_client_sees()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        using var client = new SmtpClient();

        await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None);

        Assert.Equal(["LOGIN", "PLAIN"], client.AuthenticationMechanisms.OrderBy(m => m));
        Assert.Equal(35_882_577u, client.MaxSize);
        await client.DisconnectAsync(true);
    }

    [Fact]
    public async Task A_script_that_rejects_authentication_makes_the_client_throw()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.RejectsAuth());
        using var client = new SmtpClient();

        await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None);

        await Assert.ThrowsAsync<AuthenticationException>(
            () => client.AuthenticateAsync("bob@fake.local", "wrong"));
    }

    [Fact]
    public async Task A_script_that_rejects_the_sender_makes_the_send_throw_with_the_status_code()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.RejectsSender());
        using var client = new SmtpClient();

        await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None);

        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => client.SendAsync(Message()));
        Assert.Equal(550, (int)ex.StatusCode);
    }

    [Fact]
    public async Task A_server_that_does_not_speak_smtp_produces_a_protocol_exception()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.NotSmtp());
        using var client = new SmtpClient();

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None));
    }

    [Fact]
    public async Task A_silent_server_makes_the_client_time_out()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Silent());
        using var client = new SmtpClient { Timeout = 1_000 };

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None));
    }

    [Fact]
    public async Task Without_starttls_advertised_a_client_that_requires_it_fails()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.WithoutStartTls());
        using var client = new SmtpClient();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.StartTls));
    }
}
