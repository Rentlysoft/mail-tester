using System.Net.Security;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailTester.Cli;
using MailTester.Errors;
using MailTester.Smtp;
using MimeKit;

namespace MailTester.Tests.Errors;

public class SmtpFailureExplainerTests
{
    static CliOptions Options(AuthMechanism auth = AuthMechanism.Auto, string? user = null) => new()
    {
        Host = "smtp.foo.com",
        From = MailboxAddress.Parse("a@x.com"),
        To = [MailboxAddress.Parse("b@y.com")],
        Auth = auth,
        User = user,
        Password = user is null ? null : "secreto",
    };

    static AttemptResult Failure(
        Exception exception,
        AttemptPhase phase,
        int port = 587,
        SecurityMode security = SecurityMode.StartTls,
        SslPolicyErrors certificateErrors = SslPolicyErrors.None,
        params string[] offeredMechanisms) => new()
    {
        Success = false,
        Port = port,
        Security = security,
        LastPhase = phase,
        FailedPhase = phase,
        Exception = exception,
        CertificateErrors = certificateErrors,
        AuthMechanismsOffered = offeredMechanisms,
    };

    static FailureExplanation Explain(AttemptResult result, CliOptions? options = null) =>
        SmtpFailureExplainer.Explain(result, options ?? Options());

    [Fact]
    public void Every_explanation_is_complete_enough_to_print()
    {
        var explanation = Explain(Failure(new SocketException((int)SocketError.ConnectionRefused), AttemptPhase.TcpConnect));

        Assert.False(string.IsNullOrWhiteSpace(explanation.Title));
        Assert.False(string.IsNullOrWhiteSpace(explanation.ProbableCause));
        Assert.False(string.IsNullOrWhiteSpace(explanation.TechnicalDetail));
        Assert.NotEmpty(explanation.WhatToTry);
    }

    [Fact]
    public void An_unresolvable_host_is_attributed_to_dns()
    {
        var explanation = Explain(Failure(new SocketException((int)SocketError.HostNotFound), AttemptPhase.Dns));

        Assert.Equal(AttemptPhase.Dns, explanation.Phase);
        Assert.Equal(ExitCode.NetworkFailure, explanation.ExitCode);
        Assert.Contains("smtp.foo.com", explanation.ProbableCause);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("nslookup"));
    }

    [Fact]
    public void A_refused_connection_distinguishes_itself_from_a_dropped_one()
    {
        var explanation = Explain(Failure(new SocketException((int)SocketError.ConnectionRefused), AttemptPhase.TcpConnect));

        Assert.Equal(ExitCode.NetworkFailure, explanation.ExitCode);
        Assert.Contains("rechaz", explanation.ProbableCause);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("--probe"));
    }

    [Fact]
    public void A_dropped_connection_points_at_a_firewall_rather_than_a_dead_service()
    {
        var explanation = Explain(Failure(new TimeoutException("expiró"), AttemptPhase.TcpConnect));

        Assert.Equal(ExitCode.Timeout, explanation.ExitCode);
        Assert.Contains("firewall", explanation.ProbableCause);
        // A dead service refuses; silence is the signature of a drop.
        Assert.Contains("silencio", explanation.ProbableCause);
    }

    [Fact]
    public void A_timeout_waiting_for_the_greeting_says_what_that_means()
    {
        var explanation = Explain(Failure(new TimeoutException("expiró"), AttemptPhase.Greeting));

        Assert.Equal(ExitCode.Timeout, explanation.ExitCode);
        Assert.Contains("220", explanation.ProbableCause);
    }

    [Fact]
    public void Implicit_tls_against_a_starttls_port_suggests_the_right_combination()
    {
        var result = Failure(
            new SslHandshakeException("handshake", new IOException("unexpected packet format")),
            AttemptPhase.TlsHandshake,
            port: 587,
            security: SecurityMode.Ssl);

        var explanation = Explain(result);

        Assert.Equal(ExitCode.TlsFailure, explanation.ExitCode);
        Assert.Contains("587", explanation.ProbableCause);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("--port 587") && s.Contains("--security starttls"));
        Assert.Contains(explanation.WhatToTry, s => s.Contains("--port 465") && s.Contains("--security ssl"));
    }

    [Fact]
    public void Starttls_against_an_implicit_tls_port_suggests_the_inverse()
    {
        var result = Failure(
            new SslHandshakeException("handshake"),
            AttemptPhase.TlsHandshake,
            port: 465,
            security: SecurityMode.StartTls);

        var explanation = Explain(result);

        Assert.Equal(ExitCode.TlsFailure, explanation.ExitCode);
        Assert.Contains("465", explanation.ProbableCause);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("--security ssl"));
    }

    [Fact]
    public void A_hostname_mismatch_is_explained_as_a_certificate_problem_not_a_port_problem()
    {
        var result = Failure(
            new SslHandshakeException("handshake"),
            AttemptPhase.TlsHandshake,
            certificateErrors: SslPolicyErrors.RemoteCertificateNameMismatch);

        var explanation = Explain(result);

        Assert.Contains("certificado", explanation.ProbableCause);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("--allow-invalid-cert"));
        Assert.DoesNotContain("puerto", explanation.ProbableCause);
    }

    [Fact]
    public void A_chain_error_lists_the_usual_causes()
    {
        var result = Failure(
            new SslHandshakeException("handshake"),
            AttemptPhase.TlsHandshake,
            certificateErrors: SslPolicyErrors.RemoteCertificateChainErrors);

        var explanation = Explain(result);

        Assert.Contains("autofirmado", explanation.ProbableCause);
    }

    [Fact]
    public void A_handshake_failure_with_a_valid_certificate_points_at_tls_versions()
    {
        var result = Failure(
            new SslHandshakeException("handshake"),
            AttemptPhase.TlsHandshake,
            port: 587,
            security: SecurityMode.StartTls);

        var explanation = Explain(result);

        Assert.Contains("TLS", explanation.ProbableCause);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("--probe"));
    }

    [Fact]
    public void Requiring_starttls_when_it_is_not_offered_is_a_tls_failure_with_alternatives()
    {
        var result = Failure(
            new NotSupportedException("The SMTP server does not support the STARTTLS extension."),
            AttemptPhase.Ehlo,
            security: SecurityMode.StartTls);

        var explanation = Explain(result);

        Assert.Equal(ExitCode.TlsFailure, explanation.ExitCode);
        Assert.Contains("STARTTLS", explanation.ProbableCause);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("starttls-if-available"));
    }

    [Fact]
    public void No_auth_capability_explains_that_many_servers_only_offer_it_after_starttls()
    {
        var result = Failure(
            new NotSupportedException("The SMTP server does not support authentication."),
            AttemptPhase.Authenticate,
            security: SecurityMode.None);

        var explanation = Explain(result, Options(user: "a@x.com"));

        Assert.Equal(ExitCode.AuthenticationFailure, explanation.ExitCode);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("--security starttls"));
    }

    [Fact]
    public void Rejected_credentials_mention_app_passwords_and_tenant_policy()
    {
        var result = Failure(
            new AuthenticationException("535 5.7.8 Error: authentication failed"),
            AttemptPhase.Authenticate,
            offeredMechanisms: ["LOGIN", "PLAIN"]);

        var explanation = Explain(result, Options(user: "a@x.com"));

        Assert.Equal(ExitCode.AuthenticationFailure, explanation.ExitCode);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("app password"));
        Assert.Contains(explanation.WhatToTry, s => s.Contains("Microsoft 365"));
        Assert.Contains(explanation.WhatToTry, s => s.Contains("LOGIN, PLAIN"));
    }

    [Fact]
    public void A_530_asking_for_starttls_is_not_confused_with_one_asking_for_credentials()
    {
        var result = Failure(
            new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.AuthenticationRequired,
                                    "530 5.7.0 Must issue a STARTTLS command first"),
            AttemptPhase.Authenticate,
            security: SecurityMode.None);

        var explanation = Explain(result, Options(user: "a@x.com"));

        Assert.Equal(ExitCode.AuthenticationFailure, explanation.ExitCode);
        Assert.Contains("STARTTLS", explanation.ProbableCause);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("--security starttls"));
    }

    [Fact]
    public void A_530_asking_for_credentials_says_to_pass_them()
    {
        var result = Failure(
            new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.AuthenticationRequired,
                                    "530 5.7.1 Authentication required"),
            AttemptPhase.Send,
            security: SecurityMode.StartTls);

        var explanation = Explain(result);

        Assert.Equal(ExitCode.AuthenticationFailure, explanation.ExitCode);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("--user") && s.Contains("--pass"));
        Assert.DoesNotContain("STARTTLS", explanation.ProbableCause);
    }

    [Fact]
    public void A_rejected_sender_is_explained_as_a_relay_or_ownership_problem()
    {
        var result = Failure(
            new SmtpCommandException(SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxUnavailable,
                                    MailboxAddress.Parse("a@x.com"), "550 5.7.1 Sender address rejected: not owned by user"),
            AttemptPhase.Send);

        var explanation = Explain(result);

        Assert.Equal(ExitCode.SmtpRejected, explanation.ExitCode);
        Assert.Contains("a@x.com", explanation.ProbableCause);
        Assert.Contains("remitente", explanation.ProbableCause);
    }

    [Fact]
    public void A_rejected_recipient_names_the_recipient()
    {
        var result = Failure(
            new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable,
                                    MailboxAddress.Parse("b@y.com"), "550 5.1.1 User unknown"),
            AttemptPhase.Send);

        var explanation = Explain(result);

        Assert.Equal(ExitCode.SmtpRejected, explanation.ExitCode);
        Assert.Contains("b@y.com", explanation.ProbableCause);
        Assert.Contains("destinatario", explanation.ProbableCause);
    }

    [Fact]
    public void A_message_over_the_size_limit_is_explained_as_such()
    {
        var result = Failure(
            new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.ExceededStorageAllocation,
                                    "552 5.3.4 Message size exceeds fixed limit"),
            AttemptPhase.Send);

        var explanation = Explain(result);

        Assert.Equal(ExitCode.SmtpRejected, explanation.ExitCode);
        Assert.Contains("SIZE", explanation.ProbableCause);
    }

    [Theory]
    [InlineData(SmtpStatusCode.ServiceNotAvailable)]
    [InlineData(SmtpStatusCode.MailboxBusy)]
    [InlineData(SmtpStatusCode.ErrorInProcessing)]
    public void Temporary_failures_are_flagged_as_worth_retrying(SmtpStatusCode status)
    {
        var result = Failure(
            new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, status, $"{(int)status} temporal"),
            AttemptPhase.Send);

        var explanation = Explain(result);

        Assert.Equal(ExitCode.SmtpRejected, explanation.ExitCode);
        Assert.Contains("temporal", explanation.ProbableCause);
    }

    [Fact]
    public void A_temporary_authentication_failure_is_an_auth_failure_not_a_rejection()
    {
        var result = Failure(
            new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.TemporaryAuthenticationFailure,
                                    "454 4.7.0 Temporary authentication failure"),
            AttemptPhase.Authenticate);

        var explanation = Explain(result, Options(user: "a@x.com"));

        Assert.Equal(ExitCode.AuthenticationFailure, explanation.ExitCode);
    }

    [Fact]
    public void A_server_that_does_not_speak_smtp_says_to_read_the_server_lines()
    {
        var result = Failure(new SmtpProtocolException("respuesta inesperada"), AttemptPhase.Greeting, port: 80);

        var explanation = Explain(result);

        // The bytes on the wire never parsed as SMTP at all, so TLS was never reached: this is a
        // "wrong service" failure, not a TLS one, and a script branching on exit code should be
        // sent to check what is actually listening, not to look at certificates.
        Assert.Equal(ExitCode.NetworkFailure, explanation.ExitCode);
        Assert.Contains("80", explanation.ProbableCause);
        Assert.Contains("S:", explanation.ProbableCause);
    }

    [Fact]
    public void Implicit_tls_against_a_non_conventional_port_gets_the_generic_tls_explanation()
    {
        // Port 8025 has no universal STARTTLS convention the way 587, 25 and 2525 do: a server
        // there could legitimately be doing implicit TLS on purpose, so the failure should read
        // as a generic version/cipher mismatch rather than assert "you meant STARTTLS" as fact.
        var result = Failure(
            new SslHandshakeException("handshake"),
            AttemptPhase.TlsHandshake,
            port: 8025,
            security: SecurityMode.Ssl);

        var explanation = Explain(result);

        Assert.Equal(ExitCode.TlsFailure, explanation.ExitCode);
        Assert.Contains("TLS", explanation.ProbableCause);
        Assert.DoesNotContain(explanation.WhatToTry, s => s.Contains("--security starttls"));
    }

    [Fact]
    public void A_cancelled_attempt_is_explained_as_an_interruption_not_a_failure()
    {
        var explanation = Explain(Failure(new OperationCanceledException(), AttemptPhase.TcpConnect));

        Assert.Equal(ExitCode.Unexpected, explanation.ExitCode);
        Assert.Contains("Interrumpido", explanation.ProbableCause);
        Assert.DoesNotContain("no clasificada", explanation.ProbableCause);
        Assert.Contains(explanation.WhatToTry, s => s.Contains("Volver a correr"));
    }

    [Fact]
    public void A_task_cancelled_exception_is_classified_the_same_as_a_plain_cancellation()
    {
        // TaskCanceledException derives from OperationCanceledException, and it is the concrete
        // type MailKit and the BCL actually throw, so the switch case has to match the subtype.
        var explanation = Explain(Failure(new TaskCanceledException(), AttemptPhase.Greeting));

        Assert.Equal(ExitCode.Unexpected, explanation.ExitCode);
        Assert.Contains("Interrumpido", explanation.ProbableCause);
    }

    [Fact]
    public void An_interruption_after_the_message_was_sent_warns_it_may_already_be_queued()
    {
        var result = Failure(new OperationCanceledException(), AttemptPhase.Quit) with { MessageSent = true };

        var explanation = Explain(result);

        Assert.Contains(explanation.WhatToTry, s => s.Contains("encolado"));
    }

    [Fact]
    public void An_interruption_before_sending_says_no_message_went_out()
    {
        var explanation = Explain(Failure(new OperationCanceledException(), AttemptPhase.TcpConnect));

        Assert.Contains(explanation.WhatToTry, s => s.Contains("No llegó a enviarse"));
    }

    [Fact]
    public void A_cancelled_probe_never_claims_a_message_might_be_queued()
    {
        // Probe mode never sends a message, so MessageSent cannot be true for a real probe
        // result; this exercises the options.Probe guard directly rather than relying on that
        // invariant to keep the guard itself untested.
        var result = Failure(new OperationCanceledException(), AttemptPhase.Send) with { MessageSent = true };
        var probeOptions = Options() with { Probe = true };

        var explanation = Explain(result, probeOptions);

        Assert.Contains(explanation.WhatToTry, s => s.Contains("No llegó a enviarse"));
    }

    [Fact]
    public void An_unclassified_exception_is_reported_honestly_rather_than_guessed_at()
    {
        var result = Failure(new InvalidOperationException("algo raro"), AttemptPhase.Send);

        var explanation = Explain(result);

        Assert.Equal(ExitCode.Unexpected, explanation.ExitCode);
        Assert.Contains("InvalidOperationException", explanation.TechnicalDetail);
    }

    [Fact]
    public void The_technical_detail_includes_two_levels_of_inner_exception()
    {
        var inner = new IOException("nivel 2", new SocketException((int)SocketError.ConnectionReset));
        var result = Failure(new SslHandshakeException("nivel 0", inner), AttemptPhase.TlsHandshake);

        var detail = Explain(result).TechnicalDetail;

        Assert.Contains("SslHandshakeException", detail);
        Assert.Contains("IOException", detail);
        Assert.Contains("SocketException", detail);
    }

    [Fact]
    public void Explaining_a_successful_attempt_is_a_programming_error()
    {
        var result = new AttemptResult
        {
            Success = true,
            Port = 587,
            Security = SecurityMode.StartTls,
            LastPhase = AttemptPhase.Quit,
        };

        Assert.Throws<InvalidOperationException>(() => Explain(result));
    }
}
