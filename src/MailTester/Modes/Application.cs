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

        using var log = ConsoleLogFactory.Create(options, output, out var logFileWarning);
        RunHeader.Render(log, options, logFileWarning);

        try
        {
            var code = options.Probe
                ? await ProbeRunner.RunAsync(options, log, cancellationToken)
                : await SendRunner.RunAsync(options, log, cancellationToken);

            // SmtpAttempt turns an externally cancelled token into an ordinary failed result
            // instead of throwing, so a run stopped by Ctrl+C never reaches the catch below.
            // This is the one place that still holds the caller's own token, so it is the one
            // place that can still tell "the user asked to stop" apart from a genuine failure
            // that merely happened to occur after that request.
            if (code != ExitCode.Success && cancellationToken.IsCancellationRequested)
            {
                log.Blank();
                log.Line(LogLevel.Warn, "Interrumpido antes de terminar.");
                return ExitCode.Unexpected;
            }

            return code;
        }
        catch (OperationCanceledException)
        {
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
