using MailTester.Output;
using MailTester.Smtp;

namespace MailTester.Errors;

internal static class FailureReport
{
    const int Width = ConsoleLog.RuleWidth;

    public static void Render(ConsoleLog log, FailureExplanation explanation, AttemptResult result)
    {
        log.Blank();
        log.Banner($"{explanation.Title}   (después de {result.Total.TotalMilliseconds:F0} ms)");
        log.Blank();

        log.Text("Causa más probable");
        foreach (var line in Wrap(explanation.ProbableCause))
            log.Text($"  {line}");

        log.Blank();
        log.Text("Qué probar");
        for (var i = 0; i < explanation.WhatToTry.Count; i++)
        {
            var lines = Wrap(explanation.WhatToTry[i], Width - 5);
            log.Text($"  {i + 1}) {lines[0]}");

            foreach (var continuation in lines.Skip(1))
                log.Text($"     {continuation}");
        }

        log.Blank();
        log.Text("Detalle técnico");
        foreach (var line in explanation.TechnicalDetail.ReplaceLineEndings("\n").Split('\n'))
            log.Text($"  {line}");

        log.Blank();
        var summary = explanation.Interrupted ? "INTERRUMPIDO" : "FALLA";
        log.Line(LogLevel.Fail, $"RESULTADO: {summary} en {explanation.Phase} · exit code {(int)explanation.ExitCode}");
    }

    /// <summary>Wraps on word boundaries. A word longer than the width is left intact rather
    /// than broken: a command line or a URL is more useful whole than aligned.
    ///
    /// Text that already fits is returned verbatim, spacing untouched: some suggestions line up
    /// a column of flags with extra spaces on purpose, and splitting on whitespace to measure
    /// words -- needed only to decide where an actually-too-long line has to break -- would
    /// collapse that alignment down to single spaces even when no wrapping was needed at all.</summary>
    static IReadOnlyList<string> Wrap(string text, int width = Width)
    {
        if (text.Length <= width)
            return [text];

        var lines = new List<string>();
        var current = new List<string>();
        var length = 0;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Count > 0 && length + 1 + word.Length > width)
            {
                lines.Add(string.Join(' ', current));
                current.Clear();
                length = 0;
            }

            current.Add(word);
            length += (length == 0 ? 0 : 1) + word.Length;
        }

        if (current.Count > 0)
            lines.Add(string.Join(' ', current));

        return lines.Count > 0 ? lines : [string.Empty];
    }
}
