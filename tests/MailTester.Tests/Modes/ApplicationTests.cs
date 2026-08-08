using MailTester.Errors;
using MailTester.Modes;
using MailTester.Tests.Fakes;

namespace MailTester.Tests.Modes;

public class ApplicationTests
{
    static async Task<(ExitCode Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        // --no-color keeps the real console untouched and makes the output assertable.
        var code = await Application.RunAsync([.. args, "--no-color"], output, error, CancellationToken.None);

        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Help_prints_the_usage_to_stdout_and_succeeds()
    {
        var (code, output, error) = await RunAsync("--help");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("USO", output);
        Assert.Empty(error);
    }

    [Fact]
    public async Task Invalid_arguments_go_to_stderr_with_the_argument_exit_code()
    {
        var (code, output, error) = await RunAsync("--port", "abc");

        Assert.Equal(ExitCode.InvalidArguments, code);
        Assert.Contains("--port", error);
        Assert.Contains("--help", error);
        Assert.Empty(output);
    }

    [Fact]
    public async Task No_arguments_at_all_is_an_argument_error()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await Application.RunAsync([], output, error, CancellationToken.None);

        Assert.Equal(ExitCode.InvalidArguments, code);
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task A_send_run_prints_the_header_and_returns_success()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());

        var (code, output, _) = await RunAsync(
            "--host", "127.0.0.1", "--port", server.Port.ToString(), "--security", "none",
            "--from", "a@x.com", "--to", "b@y.com", "--timeout", "5");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("mail-tester", output);
        Assert.Contains("host=127.0.0.1", output);
        Assert.Contains("ÉXITO", output);
    }

    [Fact]
    public async Task A_probe_run_is_dispatched_to_the_probe_mode()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());

        var (code, output, _) = await RunAsync(
            "--probe", "--host", "127.0.0.1", "--port", server.Port.ToString(),
            "--security", "none", "--timeout", "5");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("MATRIZ DE RESULTADOS", output);
    }

    [Fact]
    public async Task A_log_file_receives_the_same_run_that_the_console_saw()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        var path = Path.Combine(Path.GetTempPath(), $"mail-tester-{Guid.NewGuid():N}.log");

        try
        {
            var (_, output, _) = await RunAsync(
                "--host", "127.0.0.1", "--port", server.Port.ToString(), "--security", "none",
                "--from", "a@x.com", "--to", "b@y.com", "--timeout", "5", "--log-file", path);

            var logged = await File.ReadAllTextAsync(path);
            Assert.Contains("S: 220 fake.local", logged);
            Assert.Contains("ÉXITO", logged);
            Assert.Equal(output, logged);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task An_unwritable_log_file_warns_and_the_run_still_completes()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
        var impossible = Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}", "x.log");

        var (code, output, _) = await RunAsync(
            "--host", "127.0.0.1", "--port", server.Port.ToString(), "--security", "none",
            "--from", "a@x.com", "--to", "b@y.com", "--timeout", "5", "--log-file", impossible);

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("No se pudo abrir --log-file", output);
    }

    [Fact]
    public async Task A_cancelled_run_reports_the_interruption_rather_than_a_stack_trace()
    {
        using var server = FakeSmtpServer.Start(FakeSmtpScript.Silent());
        using var cancellation = new CancellationTokenSource();
        var output = new StringWriter();
        var error = new StringWriter();

        await cancellation.CancelAsync();

        var code = await Application.RunAsync(
            ["--host", "127.0.0.1", "--port", server.Port.ToString(), "--security", "none",
             "--from", "a@x.com", "--to", "b@y.com", "--no-color"],
            output, error, cancellation.Token);

        Assert.Equal(ExitCode.Unexpected, code);
        Assert.Contains("Interrumpido", output.ToString());
    }

    [Fact]
    public async Task Omitting_no_color_still_completes_with_NO_COLOR_set_in_the_environment()
    {
        // PickColorizer suppresses colour on `options.NoColor || Console.IsOutputRedirected ||
        // NO_COLOR env var set`. Every other test in this file passes --no-color, so the first
        // term is always true and short-circuits the other two: neither is ever evaluated
        // anywhere else in the suite. This test omits --no-color so evaluation can continue
        // past the first term.
        //
        // It does not, however, prove the NO_COLOR term specifically: measured directly (via a
        // temporary diagnostic assertion during development), Console.IsOutputRedirected is
        // unconditionally true inside this xunit test host, because the test runner always
        // pipes the test process's stdout to collect it. That means the second term already
        // resolves the `||` to true before the NO_COLOR term is ever reached, in this test or
        // any other in-process test. Driving true NO_COLOR-term coverage would require
        // Console.IsOutputRedirected to be false, which no unit test can force without
        // redirecting this process's real stdout handle -- that is a subprocess-level concern,
        // not something achievable here. NO_COLOR is set anyway, to document the intent and
        // because a real user relying on it would have it set; what this test actually adds is
        // coverage for the previously untested case where --no-color is absent at all.
        var previousNoColor = Environment.GetEnvironmentVariable("NO_COLOR");
        Environment.SetEnvironmentVariable("NO_COLOR", "1");

        try
        {
            using var server = FakeSmtpServer.Start(FakeSmtpScript.Working());
            var output = new StringWriter();
            var error = new StringWriter();

            var code = await Application.RunAsync(
                ["--host", "127.0.0.1", "--port", server.Port.ToString(), "--security", "none",
                 "--from", "a@x.com", "--to", "b@y.com", "--timeout", "5"],
                output, error, CancellationToken.None);

            Assert.Equal(ExitCode.Success, code);
            Assert.Contains("ÉXITO", output.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", previousNoColor);
        }
    }
}
