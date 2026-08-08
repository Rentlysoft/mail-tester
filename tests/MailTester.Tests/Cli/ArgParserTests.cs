using MailTester.Cli;

namespace MailTester.Tests.Cli;

public class ArgParserTests
{
    // The minimum a send run needs, so each test can vary one thing at a time.
    static string[] MinimalSend(params string[] extra) =>
        ["--host", "smtp.foo.com", "--from", "a@x.com", "--to", "b@y.com", .. extra];

    static CliOptions ParseOk(params string[] args)
    {
        var result = ArgParser.Parse(args);
        Assert.Empty(result.Errors);
        Assert.False(result.HelpRequested);
        return Assert.IsType<CliOptions>(result.Options);
    }

    static IReadOnlyList<string> ParseErrors(params string[] args)
    {
        var result = ArgParser.Parse(args);
        Assert.NotEmpty(result.Errors);
        Assert.Null(result.Options);
        return result.Errors;
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_short_circuits_before_any_validation(string flag)
    {
        var result = ArgParser.Parse([flag]);

        Assert.True(result.HelpRequested);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Help_wins_even_when_mixed_with_invalid_arguments()
    {
        var result = ArgParser.Parse(["--port", "abc", "--help"]);

        Assert.True(result.HelpRequested);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void No_arguments_points_the_user_at_help()
    {
        var errors = ParseErrors();

        Assert.Contains(errors, e => e.Contains("--help"));
    }

    [Fact]
    public void Defaults_are_applied_when_only_the_required_flags_are_given()
    {
        var options = ParseOk(MinimalSend());

        Assert.Equal("smtp.foo.com", options.Host);
        Assert.Equal(587, options.Port);
        Assert.False(options.PortSpecified);
        Assert.Equal(SecurityMode.Auto, options.Security);
        Assert.False(options.SecuritySpecified);
        Assert.Equal(AuthMechanism.Auto, options.Auth);
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.False(options.Probe);
        Assert.False(options.AllowInvalidCert);
        Assert.False(options.ShowSecrets);
        Assert.False(options.NoColor);
        Assert.Null(options.User);
        Assert.Null(options.Password);
        Assert.Null(options.Subject);
        Assert.Null(options.Body);
        Assert.Null(options.EhloDomain);
        Assert.Null(options.LogFile);
        Assert.False(options.ShouldAuthenticate);
    }

    [Fact]
    public void Values_can_be_attached_with_an_equals_sign()
    {
        var options = ParseOk("--host=smtp.foo.com", "--port=2525", "--from=a@x.com", "--to=b@y.com");

        Assert.Equal("smtp.foo.com", options.Host);
        Assert.Equal(2525, options.Port);
        Assert.True(options.PortSpecified);
    }

    [Fact]
    public void An_equals_sign_inside_a_value_is_part_of_the_value()
    {
        var options = ParseOk(MinimalSend("--user=a@x.com", "--pass=p=a=ss"));

        Assert.Equal("p=a=ss", options.Password);
    }

    [Fact]
    public void Repeated_to_accumulates_recipients()
    {
        var options = ParseOk("--host", "smtp.foo.com", "--from", "a@x.com",
                              "--to", "b@y.com", "--to", "c@z.com");

        Assert.Equal(["b@y.com", "c@z.com"], options.To.Select(t => t.Address));
    }

    [Fact]
    public void Specifying_port_and_security_is_recorded_for_the_probe_matrix()
    {
        var options = ParseOk(MinimalSend("--port", "465", "--security", "ssl"));

        Assert.True(options.PortSpecified);
        Assert.True(options.SecuritySpecified);
        Assert.Equal(465, options.Port);
        Assert.Equal(SecurityMode.Ssl, options.Security);
    }

    [Fact]
    public void Missing_host_is_reported()
    {
        var errors = ParseErrors("--from", "a@x.com", "--to", "b@y.com");

        Assert.Contains(errors, e => e.Contains("--host"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    public void Invalid_port_is_reported(string port)
    {
        var errors = ParseErrors(MinimalSend("--port", port));

        Assert.Contains(errors, e => e.Contains("--port"));
    }

    [Fact]
    public void Invalid_security_lists_the_valid_values()
    {
        var errors = ParseErrors(MinimalSend("--security", "tls"));

        Assert.Contains(errors, e => e.Contains(SecurityModes.ValidValues));
    }

    [Fact]
    public void Invalid_auth_lists_the_valid_values()
    {
        var errors = ParseErrors(MinimalSend("--auth", "xoauth2"));

        Assert.Contains(errors, e => e.Contains(AuthMechanisms.ValidValues));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("601")]
    [InlineData("nope")]
    public void Invalid_timeout_is_reported(string timeout)
    {
        var errors = ParseErrors(MinimalSend("--timeout", timeout));

        Assert.Contains(errors, e => e.Contains("--timeout"));
    }

    [Fact]
    public void User_without_pass_is_reported()
    {
        var errors = ParseErrors(MinimalSend("--user", "a@x.com"));

        Assert.Contains(errors, e => e.Contains("--pass"));
    }

    [Fact]
    public void Pass_without_user_is_reported()
    {
        var errors = ParseErrors(MinimalSend("--pass", "secret"));

        Assert.Contains(errors, e => e.Contains("--user"));
    }

    [Fact]
    public void Auth_none_with_a_user_and_no_pass_is_accepted_because_auth_is_skipped()
    {
        var options = ParseOk(MinimalSend("--auth", "none", "--user", "a@x.com"));

        Assert.Equal(AuthMechanism.None, options.Auth);
        Assert.False(options.ShouldAuthenticate);
    }

    [Fact]
    public void ShouldAuthenticate_is_true_only_with_credentials_and_a_mechanism()
    {
        var options = ParseOk(MinimalSend("--user", "a@x.com", "--pass", "secret"));

        Assert.True(options.ShouldAuthenticate);
    }

    [Fact]
    public void Missing_from_and_to_are_reported_in_send_mode()
    {
        var errors = ParseErrors("--host", "smtp.foo.com");

        Assert.Contains(errors, e => e.Contains("--from"));
        Assert.Contains(errors, e => e.Contains("--to"));
    }

    [Fact]
    public void From_and_to_are_optional_in_probe_mode()
    {
        var options = ParseOk("--host", "smtp.foo.com", "--probe");

        Assert.True(options.Probe);
        Assert.Null(options.From);
        Assert.Empty(options.To);
    }

    [Fact]
    public void Probe_lowers_the_default_timeout()
    {
        var options = ParseOk("--host", "smtp.foo.com", "--probe");

        Assert.Equal(10, options.TimeoutSeconds);
    }

    [Fact]
    public void Probe_still_honours_an_explicit_timeout()
    {
        var options = ParseOk("--host", "smtp.foo.com", "--probe", "--timeout", "45");

        Assert.Equal(45, options.TimeoutSeconds);
    }

    [Theory]
    [InlineData("--subject", "hola")]
    [InlineData("--body", "texto")]
    public void Probe_rejects_message_content_flags(string flag, string value)
    {
        var errors = ParseErrors("--host", "smtp.foo.com", "--probe", flag, value);

        Assert.Contains(errors, e => e.Contains(flag) && e.Contains("--probe"));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("a@")]
    [InlineData("@x.com")]
    public void Malformed_from_is_reported_before_any_connection(string address)
    {
        var errors = ParseErrors("--host", "smtp.foo.com", "--from", address, "--to", "b@y.com");

        Assert.Contains(errors, e => e.Contains("--from") && e.Contains(address));
    }

    [Fact]
    public void Malformed_to_is_reported()
    {
        var errors = ParseErrors("--host", "smtp.foo.com", "--from", "a@x.com", "--to", "nope");

        Assert.Contains(errors, e => e.Contains("--to") && e.Contains("nope"));
    }

    [Fact]
    public void A_display_name_is_accepted_in_addresses()
    {
        var options = ParseOk("--host", "smtp.foo.com", "--from=Pablo Russo <a@x.com>", "--to", "b@y.com");

        Assert.Equal("a@x.com", options.From!.Address);
        Assert.Equal("Pablo Russo", options.From!.Name);
    }

    [Fact]
    public void Unknown_flags_are_rejected_rather_than_ignored()
    {
        var errors = ParseErrors(MinimalSend("--verbose"));

        Assert.Contains(errors, e => e.Contains("--verbose"));
    }

    [Fact]
    public void An_unknown_flag_with_an_inline_value_does_not_echo_the_value()
    {
        var errors = ParseErrors(MinimalSend("--pas=hunter2"));

        Assert.Contains(errors, e => e.Contains("--pas"));
        Assert.DoesNotContain(errors, e => e.Contains("hunter2"));
    }

    [Fact]
    public void A_stray_value_left_over_from_a_misspelled_flag_is_not_echoed()
    {
        // "--passs" (typo) is rejected as an unknown flag on its own, which leaves "hunter2" -- a
        // password that was meant to be its value -- looking like a second, standalone argument.
        var errors = ParseErrors(MinimalSend("--passs", "hunter2"));

        Assert.DoesNotContain(errors, e => e.Contains("hunter2"));
        Assert.Contains(errors, e => e.Contains("--help"));
    }

    [Fact]
    public void A_stray_value_that_itself_starts_with_a_dash_is_not_echoed()
    {
        // "-hunter2" looks exactly like a second, independent unknown flag once "--passs" has
        // already been rejected -- but it is far more likely the password the user meant to pass
        // to the flag they just mistyped, so it must not be read back to them either.
        var errors = ParseErrors(MinimalSend("--passs", "-hunter2"));

        Assert.DoesNotContain(errors, e => e.Contains("hunter2"));
        Assert.Contains(errors, e => e.Contains("--passs"));
        Assert.Contains(errors, e => e.Contains("posición"));
    }

    [Fact]
    public void A_single_unknown_flag_is_still_named()
    {
        var errors = ParseErrors(MinimalSend("--passs", "hunter2"));

        Assert.Contains(errors, e => e.Contains("--passs"));
    }

    [Fact]
    public void A_recognised_flag_right_after_an_unknown_one_is_still_parsed()
    {
        var errors = ParseErrors(MinimalSend("--verbose", "--port", "25"));

        Assert.Contains(errors, e => e.Contains("--verbose"));
        Assert.DoesNotContain(errors, e => e.Contains("--port"));
        Assert.DoesNotContain(errors, e => e.Contains("posición"));
    }

    [Fact]
    public void A_flag_missing_its_value_at_the_end_is_reported()
    {
        var errors = ParseErrors("--host");

        Assert.Contains(errors, e => e.Contains("--host") && e.Contains("valor"));
    }

    [Fact]
    public void A_repeated_scalar_flag_is_an_error_rather_than_last_one_wins()
    {
        var errors = ParseErrors(MinimalSend("--port", "25", "--port", "587"));

        Assert.Contains(errors, e => e.Contains("--port"));
    }

    [Fact]
    public void Boolean_flags_are_parsed()
    {
        var options = ParseOk(MinimalSend("--allow-invalid-cert", "--show-secrets", "--no-color"));

        Assert.True(options.AllowInvalidCert);
        Assert.True(options.ShowSecrets);
        Assert.True(options.NoColor);
    }

    [Fact]
    public void Optional_string_flags_are_parsed()
    {
        var options = ParseOk(MinimalSend(
            "--subject", "asunto", "--body", "cuerpo",
            "--ehlo-domain", "mi-host", "--log-file", "c:/tmp/log.txt"));

        Assert.Equal("asunto", options.Subject);
        Assert.Equal("cuerpo", options.Body);
        Assert.Equal("mi-host", options.EhloDomain);
        Assert.Equal("c:/tmp/log.txt", options.LogFile);
    }

    [Fact]
    public void Every_error_is_collected_rather_than_failing_on_the_first()
    {
        var errors = ParseErrors("--port", "abc", "--security", "tls");

        Assert.True(errors.Count >= 4, $"Se esperaban al menos 4 errores, hubo {errors.Count}");
    }
}
