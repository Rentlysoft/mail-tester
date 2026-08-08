using MailTester.Cli;
using MailTester.Errors;
using MailTester.Output;

namespace MailTester.Modes;

/// <summary>
/// The whole run, with its writers injected so it can be exercised end to end in tests.
/// Program.Main is only the adapter that supplies the real console.
/// </summary>
internal static class Application
{
    public static async Task<ExitCode> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var parse = ArgParser.Parse(args);

        if (parse.HelpRequested)
        {
            output.Write(HelpText.Render());
            return ExitCode.Success;
        }

        if (parse.Errors.Count > 0)
        {
            error.WriteLine("Argumentos inválidos:");

            foreach (var message in parse.Errors)
                error.WriteLine($"  - {message}");

            error.WriteLine();
            error.WriteLine("Corré 'mail-tester --help' para ver el uso.");
            return ExitCode.InvalidArguments;
        }

        // Errors is empty and help was not requested, so the parser produced options.
        var options = parse.Options!;

        using var log = ConsoleLogFactory.Create(options, output, Console.IsOutputRedirected, out var logFileWarning);
        RunHeader.Render(log, options, logFileWarning);

        try
        {
            return options.Probe
                ? await ProbeRunner.RunAsync(options, log, cancellationToken)
                : await SendRunner.RunAsync(options, log, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Unreachable today: SmtpAttempt hands its own cancellation back as an ordinary
            // failed AttemptResult instead of throwing, and SmtpFailureExplainer now classifies
            // that result as an interruption on its own, so SendRunner and ProbeRunner always
            // return normally even on Ctrl+C. This stays as a safety net for a future caller
            // that does let cancellation escape as an exception -- e.g. a step added here,
            // outside SmtpAttempt, that is itself cancellable.
            log.Blank();
            log.Line(LogLevel.Warn, "Interrumpido antes de terminar.");
            return ExitCode.Unexpected;
        }
        catch (Exception ex)
        {
            log.Blank();
            log.Banner("ERROR INESPERADO EN LA HERRAMIENTA");
            log.Line(LogLevel.Fail, $"{ex.GetType().FullName}: {ex.Message}");

            foreach (var line in (ex.StackTrace ?? string.Empty).ReplaceLineEndings("\n").Split('\n'))
                log.Text(line);

            return ExitCode.Unexpected;
        }
    }
}
