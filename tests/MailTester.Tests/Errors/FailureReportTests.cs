using MailTester.Errors;
using MailTester.Output;
using MailTester.Smtp;
using MailTester.Cli;

namespace MailTester.Tests.Errors;

public class FailureReportTests
{
    static (string Text, FailureExplanation Explanation) Render(string cause, params string[] suggestions)
    {
        var output = new StringWriter();
        using var log = new ConsoleLog(output, null, new NullColorizer(), () => TimeSpan.FromSeconds(2));

        var explanation = new FailureExplanation(
            "FALLA EN FASE: HANDSHAKE TLS",
            AttemptPhase.TlsHandshake,
            ExitCode.TlsFailure,
            cause,
            suggestions,
            "SslHandshakeException: handshake roto\n  -> IOException: unexpected packet format");

        var result = new AttemptResult
        {
            Success = false,
            Port = 587,
            Security = SecurityMode.Ssl,
            LastPhase = AttemptPhase.TlsHandshake,
            FailedPhase = AttemptPhase.TlsHandshake,
            Total = TimeSpan.FromMilliseconds(1204),
        };

        FailureReport.Render(log, explanation, result);
        return (output.ToString(), explanation);
    }

    [Fact]
    public void The_report_carries_the_title_the_elapsed_time_and_the_exit_code()
    {
        var (text, _) = Render("causa breve", "hacer esto");

        Assert.Contains("FALLA EN FASE: HANDSHAKE TLS", text);
        Assert.Contains("1204 ms", text);
        Assert.Contains("exit code 4", text);
    }

    [Fact]
    public void An_interrupted_run_is_summarised_as_interrupted_not_as_a_failure()
    {
        var output = new StringWriter();
        using var log = new ConsoleLog(output, null, new NullColorizer(), () => TimeSpan.FromSeconds(1));

        var explanation = new FailureExplanation(
            "DIAGNÓSTICO INTERRUMPIDO",
            AttemptPhase.TcpConnect,
            ExitCode.Unexpected,
            "Interrumpido antes de terminar: esto no es una falla del servidor ni de la configuración.",
            ["Volver a correr el mismo comando."],
            "TaskCanceledException: A task was canceled.",
            Interrupted: true);

        var result = new AttemptResult
        {
            Success = false,
            Port = 587,
            Security = SecurityMode.None,
            LastPhase = AttemptPhase.TcpConnect,
            FailedPhase = AttemptPhase.TcpConnect,
            Total = TimeSpan.FromMilliseconds(25),
        };

        FailureReport.Render(log, explanation, result);
        var text = output.ToString();

        // The block must not contradict itself: the cause already says this was not a failure,
        // so the summary line has to agree instead of falling back to the word it just denied.
        Assert.Contains("RESULTADO: INTERRUMPIDO en TcpConnect", text);
        Assert.DoesNotContain("RESULTADO: FALLA", text);
    }

    [Fact]
    public void The_sections_are_labelled_so_the_reader_knows_what_is_advice()
    {
        var (text, _) = Render("causa breve", "hacer esto");

        Assert.Contains("Causa más probable", text);
        Assert.Contains("Qué probar", text);
        Assert.Contains("Detalle técnico", text);
    }

    [Fact]
    public void Suggestions_are_numbered()
    {
        var (text, _) = Render("causa", "primero", "segundo", "tercero");

        Assert.Contains("1) primero", text);
        Assert.Contains("2) segundo", text);
        Assert.Contains("3) tercero", text);
    }

    [Fact]
    public void A_long_cause_is_wrapped_instead_of_running_off_the_terminal()
    {
        var cause = string.Join(" ", Enumerable.Repeat("palabra", 60));

        var (text, _) = Render(cause, "hacer esto");

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        Assert.All(lines, line => Assert.True(line.Length <= 82, $"Línea demasiado larga ({line.Length}): {line}"));
        Assert.Contains(lines, line => line.Contains("palabra palabra"));
    }

    [Fact]
    public void Wrapping_never_splits_a_word()
    {
        var cause = string.Join(" ", Enumerable.Repeat("palabralargaquenoseparte", 12));

        var (text, _) = Render(cause, "x");

        Assert.DoesNotContain("palabralargaquenopart\n", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_technical_detail_keeps_its_own_line_breaks()
    {
        var (text, _) = Render("causa", "x");

        Assert.Contains("SslHandshakeException: handshake roto", text);
        Assert.Contains("-> IOException: unexpected packet format", text);
    }

    [Fact]
    public void A_long_suggestion_wraps_with_its_numbering_intact_and_continuations_aligned()
    {
        var longSuggestion = string.Join(" ", Enumerable.Repeat("consejo", 20));
        var (text, _) = Render("causa", "primero", longSuggestion, "tercero");

        var lines = text.ReplaceLineEndings("\n").Split('\n');

        var itemLine = Assert.Single(lines, line => line.StartsWith("  2) "));
        var continuations = lines.Where(line => line.StartsWith("     consejo")).ToArray();

        // The numbered prefix ("  2) ") and the continuation indent ("     ") are both five
        // columns wide, so a wrapped line's text lines up under the item's own text rather
        // than under its number.
        Assert.Equal(5, itemLine.IndexOf("consejo", StringComparison.Ordinal));
        Assert.NotEmpty(continuations);

        // All twenty words of the suggestion survive whole, split across the item line and
        // its continuations: none dropped, none broken mid-word.
        var wordCount = new[] { itemLine }.Concat(continuations)
            .Sum(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(word => word == "consejo"));
        Assert.Equal(20, wordCount);

        // The suggestions around the wrapped one keep their own numbering.
        Assert.Contains(lines, line => line.Contains("1) primero"));
        Assert.Contains(lines, line => line.Contains("3) tercero"));
    }
}
