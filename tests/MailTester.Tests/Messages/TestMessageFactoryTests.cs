using MailTester.Cli;
using MailTester.Messages;
using MimeKit;

namespace MailTester.Tests.Messages;

public class TestMessageFactoryTests
{
    static readonly MessageContext Context = new(
        new DateTimeOffset(2026, 8, 7, 14, 32, 18, TimeSpan.Zero),
        "smtp.foo.com:587 (StartTls)",
        "PLAIN como a@x.com",
        "desktop-abc (10.0.0.5:51422)");

    static CliOptions Options(string? subject = null, string? body = null, params string[] recipients) => new()
    {
        Host = "smtp.foo.com",
        From = MailboxAddress.Parse("a@x.com"),
        To = [.. (recipients.Length == 0 ? ["b@y.com"] : recipients).Select(MailboxAddress.Parse)],
        Subject = subject,
        Body = body,
    };

    static string BodyOf(MimeMessage message) => Assert.IsType<TextPart>(message.Body).Text;

    [Fact]
    public void Sender_and_every_recipient_are_carried_over()
    {
        var message = TestMessageFactory.Create(Options(recipients: ["b@y.com", "c@z.com"]), Context);

        Assert.Equal(["a@x.com"], message.From.Mailboxes.Select(m => m.Address));
        Assert.Equal(["b@y.com", "c@z.com"], message.To.Mailboxes.Select(m => m.Address));
    }

    [Fact]
    public void The_default_subject_carries_a_utc_timestamp()
    {
        var message = TestMessageFactory.Create(Options(), Context);

        Assert.Equal("mail-tester 2026-08-07T14:32:18Z", message.Subject);
    }

    [Fact]
    public void An_explicit_subject_wins()
    {
        var message = TestMessageFactory.Create(Options(subject: "asunto propio"), Context);

        Assert.Equal("asunto propio", message.Subject);
    }

    [Fact]
    public void The_default_body_states_everything_needed_to_trace_the_message()
    {
        var body = BodyOf(TestMessageFactory.Create(Options(), Context));

        Assert.Contains("2026-08-07T14:32:18Z", body);
        Assert.Contains("smtp.foo.com:587 (StartTls)", body);
        Assert.Contains("PLAIN como a@x.com", body);
        Assert.Contains("desktop-abc (10.0.0.5:51422)", body);
    }

    [Fact]
    public void An_explicit_body_replaces_the_default_entirely()
    {
        var body = BodyOf(TestMessageFactory.Create(Options(body: "solo esto"), Context));

        Assert.Equal("solo esto", body);
    }

    [Fact]
    public void The_message_is_plain_text_with_no_attachments()
    {
        var message = TestMessageFactory.Create(Options(), Context);

        var part = Assert.IsType<TextPart>(message.Body);
        Assert.True(part.IsPlain);
        Assert.Empty(message.Attachments);
    }

    [Fact]
    public void The_mailer_header_identifies_the_tool()
    {
        var message = TestMessageFactory.Create(Options(), Context);

        Assert.Equal("mail-tester", message.Headers["X-Mailer"]);
    }

    [Fact]
    public void A_message_id_is_present_so_the_send_can_be_traced_in_server_logs()
    {
        var message = TestMessageFactory.Create(Options(), Context);

        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
    }

    [Fact]
    public void The_date_header_matches_the_context_timestamp()
    {
        var message = TestMessageFactory.Create(Options(), Context);

        Assert.Equal(Context.Timestamp, message.Date);
    }

    [Fact]
    public void Building_a_message_without_a_sender_is_a_programming_error_not_a_silent_send()
    {
        var options = new CliOptions { Host = "smtp.foo.com", To = [MailboxAddress.Parse("b@y.com")] };

        Assert.Throws<InvalidOperationException>(() => TestMessageFactory.Create(options, Context));
    }
}
