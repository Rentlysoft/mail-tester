using MailTester.Cli;
using MailTester.Errors;
using MailTester.Output;
using MailTester.Smtp;

namespace MailTester.Modes;

internal static class ProbeRunner
{
    public static async Task<ExitCode> RunAsync(CliOptions options, ConsoleLog log, CancellationToken cancellationToken)
    {
        var combinations = ProbeMatrix.Build(options);
        log.Line(LogLevel.Info, $"Modo probe: {combinations.Count} combinación(es), hasta {options.TimeoutSeconds}s cada una. No se envía ningún mensaje.");

        var results = new List<AttemptResult>();

        // Sequential on purpose: running these in parallel would interleave the protocol
        // dialogues, and reading the dialogue is the point of the tool.
        foreach (var combination in combinations)
        {
            log.Blank();
            log.Banner($"INTENTO {results.Count + 1}/{combinations.Count} — puerto {combination.Port}, security {combination.Security.ToCliName()}");

            var attempt = new SmtpAttempt(options, log);
            results.Add(await attempt.RunAsync(combination.Port, combination.Security, sendMessage: false, cancellationToken));

            // A real Ctrl+C surfaces here as an ordinary failed AttemptResult -- SmtpAttempt
            // only recognises its own timeout as special, not the caller's token -- so the loop
            // has to check explicitly instead of trusting the result to say "stop".
            if (cancellationToken.IsCancellationRequested)
                break;
        }

        var recommended = ProbeMatrix.Recommend(results, options.ShouldAuthenticate);
        ProbeReport.Render(log, options, results, recommended);

        if (recommended is not null)
            return ExitCode.Success;

        var worst = ProbeMatrix.MostAdvancedFailure(results);
        if (worst is null)
        {
            // Recommend found nothing worked, so MostAdvancedFailure runs over the same set and
            // filters to failures: it can only come back empty if every result already succeeded,
            // which would have made Recommend return one of them above.
            log.Line(LogLevel.Fail, "Ninguna combinación funcionó, pero no se encontró ninguna falla para explicar. Esto no debería pasar.");
            return ExitCode.Unexpected;
        }

        var explanation = SmtpFailureExplainer.Explain(worst, options);
        FailureReport.Render(log, explanation, worst);

        return explanation.ExitCode;
    }
}
