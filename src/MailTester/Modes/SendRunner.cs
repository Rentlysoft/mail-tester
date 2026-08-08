using MailTester.Cli;
using MailTester.Errors;
using MailTester.Output;
using MailTester.Smtp;

namespace MailTester.Modes;

internal static class SendRunner
{
    public static async Task<ExitCode> RunAsync(CliOptions options, ConsoleLog log, CancellationToken cancellationToken)
    {
        var attempt = new SmtpAttempt(options, log);
        var result = await attempt.RunAsync(options.Port, options.Security, sendMessage: true, cancellationToken);

        if (!result.Success)
        {
            var explanation = SmtpFailureExplainer.Explain(result, options);
            FailureReport.Render(log, explanation, result);
            return explanation.ExitCode;
        }

        log.Blank();
        log.Line(LogLevel.Ok, $"RESULTADO: ÉXITO · total {result.Total.TotalMilliseconds:F0} ms · exit code 0");
        log.Line(LogLevel.Info, $"Respuesta del servidor: {result.ServerResponse}");
        log.Line(LogLevel.Info, $"Buscá este Message-Id en los logs del servidor si el mensaje no aparece: {result.MessageId}");

        return ExitCode.Success;
    }
}
