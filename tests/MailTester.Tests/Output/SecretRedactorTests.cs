using MailTester.Output;

namespace MailTester.Tests.Output;

public class SecretRedactorTests
{
    [Fact]
    public void Plain_hides_the_payload_and_keeps_the_mechanism_visible()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        var line = redactor.Client("AUTH PLAIN AGJvYkBmYWtlLmxvY2FsAHMzY3IzdC1wNHNz");

        Assert.Equal("AUTH PLAIN ***REDACTED***", line);
    }

    [Fact]
    public void An_auth_command_without_a_payload_passes_through()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        Assert.Equal("AUTH LOGIN", redactor.Client("AUTH LOGIN"));
    }

    [Fact]
    public void Login_challenge_responses_are_hidden_even_without_an_auth_prefix()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        redactor.Client("AUTH LOGIN");
        redactor.Server("334 VXNlcm5hbWU6");
        var user = redactor.Client("Ym9iQGZha2UubG9jYWw=");
        redactor.Server("334 UGFzc3dvcmQ6");
        var password = redactor.Client("czNjcjN0LXA0c3M=");

        Assert.Equal("***REDACTED***", user);
        Assert.Equal("***REDACTED***", password);
    }

    [Fact]
    public void Cram_md5_response_is_hidden()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        redactor.Client("AUTH CRAM-MD5");
        redactor.Server("334 PDEyMzQ1Njc4OUBmYWtlLmxvY2FsPg==");
        var response = redactor.Client("Ym9iIDY5N2Y2YmNkZjliZDNmMWU2ZjhlYTU1NDdjMTk4NmY0");

        Assert.Equal("***REDACTED***", response);
    }

    [Fact]
    public void Server_responses_are_never_redacted_because_their_codes_are_the_diagnosis()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        redactor.Client("AUTH PLAIN AGJvYgBwYXNz");

        Assert.Equal("535 5.7.8 Error: authentication failed", redactor.Server("535 5.7.8 Error: authentication failed"));
    }

    [Theory]
    [InlineData("235 2.7.0 Authentication successful")]
    [InlineData("535 5.7.8 Error: authentication failed")]
    [InlineData("454 4.7.0 Temporary authentication failure")]
    public void A_final_response_closes_the_exchange_so_later_commands_stay_visible(string finalResponse)
    {
        var redactor = new SecretRedactor(showSecrets: false);

        redactor.Client("AUTH PLAIN AGJvYgBwYXNz");
        redactor.Server(finalResponse);

        Assert.Equal("MAIL FROM:<a@x.com>", redactor.Client("MAIL FROM:<a@x.com>"));
    }

    [Fact]
    public void A_334_challenge_keeps_the_exchange_open()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        redactor.Client("AUTH LOGIN");
        redactor.Server("334 VXNlcm5hbWU6");

        Assert.Equal("***REDACTED***", redactor.Client("Ym9i"));
    }

    [Fact]
    public void A_334_continuation_with_a_trailing_dash_still_keeps_the_exchange_open()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        redactor.Client("AUTH LOGIN");
        redactor.Server("334-Username:");

        Assert.Equal("***REDACTED***", redactor.Client("Ym9i"));
    }

    [Fact]
    public void A_server_line_with_no_parseable_status_code_does_not_strand_the_exchange_open()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        redactor.Client("AUTH LOGIN");
        redactor.Server("<html>this is not an SMTP reply</html>");
        redactor.Server("235 2.7.0 Authentication successful");

        Assert.Equal("MAIL FROM:<a@x.com>", redactor.Client("MAIL FROM:<a@x.com>"));
    }

    [Fact]
    public void Commands_before_any_auth_are_untouched()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        Assert.Equal("EHLO desktop-abc", redactor.Client("EHLO desktop-abc"));
        Assert.Equal("STARTTLS", redactor.Client("STARTTLS"));
    }

    [Fact]
    public void A_command_that_merely_starts_with_the_letters_auth_is_not_an_auth_command()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        Assert.Equal("AUTHENTICATE something", redactor.Client("AUTHENTICATE something"));
    }

    [Fact]
    public void Show_secrets_disables_redaction_entirely()
    {
        var redactor = new SecretRedactor(showSecrets: true);

        redactor.Client("AUTH LOGIN");
        redactor.Server("334 VXNlcm5hbWU6");

        Assert.Equal("Ym9iQGZha2UubG9jYWw=", redactor.Client("Ym9iQGZha2UubG9jYWw="));
    }

    [Fact]
    public void Redaction_is_case_insensitive_about_the_auth_verb()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        Assert.Equal("auth plain ***REDACTED***", redactor.Client("auth plain AGJvYgA="));
    }

    [Fact]
    public void A_tab_separated_auth_command_still_masks_the_payload()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        Assert.Equal("AUTH\tPLAIN\t***REDACTED***", redactor.Client("AUTH\tPLAIN\tAGJvYgBwYXNz"));
    }

    [Fact]
    public void Leading_whitespace_before_auth_does_not_defeat_recognition()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        Assert.Equal(" AUTH PLAIN ***REDACTED***", redactor.Client(" AUTH PLAIN AGJvYgBwYXNz"));
    }

    [Fact]
    public void Extra_whitespace_between_auth_and_the_mechanism_keeps_the_mechanism_visible()
    {
        var redactor = new SecretRedactor(showSecrets: false);

        Assert.Equal("AUTH  PLAIN ***REDACTED***", redactor.Client("AUTH  PLAIN AGJvYgBwYXNz"));
    }
}
