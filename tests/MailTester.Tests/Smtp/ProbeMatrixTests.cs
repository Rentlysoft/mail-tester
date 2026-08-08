using MailTester.Cli;
using MailTester.Smtp;

namespace MailTester.Tests.Smtp;

public class ProbeMatrixTests
{
    static CliOptions Options(int? port = null, SecurityMode? security = null) => new()
    {
        Host = "smtp.foo.com",
        Port = port ?? 587,
        PortSpecified = port is not null,
        Security = security ?? SecurityMode.Auto,
        SecuritySpecified = security is not null,
        Probe = true,
    };

    static AttemptResult Result(
        int port,
        SecurityMode security,
        bool success = true,
        bool secure = false,
        bool authenticated = false,
        int totalMs = 100,
        AttemptPhase failedPhase = AttemptPhase.TcpConnect) => new()
    {
        Success = success,
        Port = port,
        Security = security,
        Secure = secure,
        Authenticated = authenticated,
        LastPhase = success ? AttemptPhase.Quit : failedPhase,
        FailedPhase = success ? null : failedPhase,
        Exception = success ? null : new InvalidOperationException("falló"),
        Total = TimeSpan.FromMilliseconds(totalMs),
    };

    [Fact]
    public void The_default_matrix_is_the_curated_list_of_nine_combinations()
    {
        var combinations = ProbeMatrix.Build(Options());

        Assert.Equal(9, combinations.Count);
        Assert.Equal(
            [
                new ProbeCombination(25, SecurityMode.None),
                new ProbeCombination(25, SecurityMode.StartTls),
                new ProbeCombination(587, SecurityMode.StartTls),
                new ProbeCombination(587, SecurityMode.None),
                new ProbeCombination(587, SecurityMode.Ssl),
                new ProbeCombination(465, SecurityMode.Ssl),
                new ProbeCombination(465, SecurityMode.StartTls),
                new ProbeCombination(2525, SecurityMode.StartTls),
                new ProbeCombination(2525, SecurityMode.None),
            ],
            combinations);
    }

    [Fact]
    public void Naming_a_port_narrows_the_matrix_to_that_port()
    {
        var combinations = ProbeMatrix.Build(Options(port: 1025));

        Assert.All(combinations, c => Assert.Equal(1025, c.Port));
        Assert.Equal(
            [SecurityMode.StartTls, SecurityMode.Ssl, SecurityMode.None],
            combinations.Select(c => c.Security));
    }

    [Fact]
    public void Naming_a_security_mode_narrows_the_matrix_to_that_mode()
    {
        var combinations = ProbeMatrix.Build(Options(security: SecurityMode.Ssl));

        Assert.All(combinations, c => Assert.Equal(SecurityMode.Ssl, c.Security));
        Assert.Equal([25, 587, 465, 2525], combinations.Select(c => c.Port));
    }

    [Fact]
    public void Naming_both_reduces_the_matrix_to_a_single_attempt()
    {
        var combinations = ProbeMatrix.Build(Options(port: 465, security: SecurityMode.Ssl));

        Assert.Equal([new ProbeCombination(465, SecurityMode.Ssl)], combinations);
    }

    [Fact]
    public void The_recommendation_prefers_an_encrypted_combination_over_a_faster_plaintext_one()
    {
        var results = new[]
        {
            Result(25, SecurityMode.None, secure: false, totalMs: 50),
            Result(587, SecurityMode.StartTls, secure: true, totalMs: 300),
        };

        var recommended = ProbeMatrix.Recommend(results, credentialsGiven: false);

        Assert.Equal(587, recommended!.Port);
    }

    [Fact]
    public void Among_encrypted_combinations_the_fastest_wins()
    {
        var results = new[]
        {
            Result(465, SecurityMode.Ssl, secure: true, totalMs: 400),
            Result(587, SecurityMode.StartTls, secure: true, totalMs: 290),
        };

        var recommended = ProbeMatrix.Recommend(results, credentialsGiven: false);

        Assert.Equal(587, recommended!.Port);
    }

    [Fact]
    public void With_credentials_a_combination_that_did_not_authenticate_is_not_recommended()
    {
        var results = new[]
        {
            Result(25, SecurityMode.None, secure: true, authenticated: false, totalMs: 50),
            Result(587, SecurityMode.StartTls, secure: true, authenticated: true, totalMs: 300),
        };

        var recommended = ProbeMatrix.Recommend(results, credentialsGiven: true);

        Assert.Equal(587, recommended!.Port);
    }

    [Fact]
    public void Without_credentials_reaching_the_ehlo_is_enough_to_be_recommended()
    {
        var results = new[] { Result(587, SecurityMode.StartTls, secure: true, authenticated: false) };

        Assert.NotNull(ProbeMatrix.Recommend(results, credentialsGiven: false));
    }

    [Fact]
    public void Nothing_is_recommended_when_nothing_worked()
    {
        var results = new[]
        {
            Result(25, SecurityMode.None, success: false),
            Result(587, SecurityMode.StartTls, success: false),
        };

        Assert.Null(ProbeMatrix.Recommend(results, credentialsGiven: false));
    }

    [Fact]
    public void The_most_advanced_failure_is_the_one_worth_explaining()
    {
        var results = new[]
        {
            Result(2525, SecurityMode.None, success: false, failedPhase: AttemptPhase.TcpConnect),
            Result(587, SecurityMode.StartTls, success: false, failedPhase: AttemptPhase.Authenticate),
            Result(25, SecurityMode.None, success: false, failedPhase: AttemptPhase.Greeting),
        };

        var worst = ProbeMatrix.MostAdvancedFailure(results);

        Assert.NotNull(worst);
        Assert.Equal(AttemptPhase.Authenticate, worst.FailedPhase);
        Assert.Equal(587, worst.Port);
    }

    [Fact]
    public void Most_advanced_failure_filters_to_failures_and_ignores_successes()
    {
        var results = new[]
        {
            Result(25, SecurityMode.None, success: true),
            Result(587, SecurityMode.StartTls, success: false, failedPhase: AttemptPhase.Authenticate),
            Result(465, SecurityMode.Ssl, success: true),
        };

        var worst = ProbeMatrix.MostAdvancedFailure(results);

        Assert.NotNull(worst);
        Assert.False(worst.Success);
        Assert.Equal(AttemptPhase.Authenticate, worst.FailedPhase);
    }

    [Fact]
    public void With_credentials_nothing_is_recommended_when_nothing_authenticated()
    {
        var results = new[]
        {
            Result(25, SecurityMode.None, secure: true, authenticated: false),
            Result(587, SecurityMode.StartTls, secure: true, authenticated: false),
        };

        var recommended = ProbeMatrix.Recommend(results, credentialsGiven: true);

        Assert.Null(recommended);
    }

    [Fact]
    public void Most_advanced_failure_ranks_ssl_with_completed_handshake_ahead_of_plaintext_at_same_phase()
    {
        var results = new[]
        {
            Result(465, SecurityMode.Ssl, success: false, failedPhase: AttemptPhase.Greeting, totalMs: 200),
            Result(25, SecurityMode.None, success: false, failedPhase: AttemptPhase.Greeting, totalMs: 100),
        };

        var worst = ProbeMatrix.MostAdvancedFailure(results);

        Assert.NotNull(worst);
        Assert.Equal(465, worst.Port);
        Assert.Equal(SecurityMode.Ssl, worst.Security);
    }
}
