namespace MailTester.Output;

/// <summary>
/// The single place anything reaches the screen. Console output is coloured; the
/// optional log file gets the same text without any colour control.
/// </summary>
internal sealed class ConsoleLog(TextWriter output, TextWriter? file, IColorizer colorizer, Func<TimeSpan> elapsed)
    : IDisposable
{
    const int RuleWidth = 64;

    public void Line(LogLevel level, string message) =>
        Write(ColorFor(level), $"[{elapsed():mm\\:ss\\.fff}] {Label(level),-4}  {message}");

    /// <summary>A raw SMTP dialogue line. No timestamp: these read as the conversation they are.</summary>
    public void Protocol(string prefix, string line) => Write(ConsoleColor.DarkGray, prefix + line);

    /// <summary>A plain line with no timestamp or level, for multi-line explanation blocks.</summary>
    public void Text(string line) => Write(null, line);

    public void Banner(string title)
    {
        var rule = new string('═', RuleWidth);
        Write(ConsoleColor.White, rule);
        Write(ConsoleColor.White, "  " + title);
        Write(ConsoleColor.White, rule);
    }

    public void Blank() => Write(null, string.Empty);

    public void Dispose() => file?.Dispose();

    void Write(ConsoleColor? color, string text)
    {
        if (color is { } value)
        {
            colorizer.Set(value);
            output.WriteLine(text);
            colorizer.Reset();
        }
        else
        {
            output.WriteLine(text);
        }

        file?.WriteLine(text);
    }

    static string Label(LogLevel level) => level switch
    {
        LogLevel.Info => "INFO",
        LogLevel.Conf => "CONF",
        LogLevel.Step => "STEP",
        LogLevel.Ok => "OK",
        LogLevel.Caps => "CAPS",
        LogLevel.Cert => "CERT",
        LogLevel.Warn => "WARN",
        LogLevel.Fail => "FAIL",
        LogLevel.Sent => "SENT",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unlabelled log level"),
    };

    static ConsoleColor ColorFor(LogLevel level) => level switch
    {
        LogLevel.Info => ConsoleColor.Gray,
        LogLevel.Conf => ConsoleColor.DarkGray,
        LogLevel.Step => ConsoleColor.Cyan,
        LogLevel.Ok => ConsoleColor.Green,
        LogLevel.Caps => ConsoleColor.DarkGray,
        LogLevel.Cert => ConsoleColor.Yellow,
        LogLevel.Warn => ConsoleColor.Yellow,
        LogLevel.Fail => ConsoleColor.Red,
        LogLevel.Sent => ConsoleColor.Green,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Uncoloured log level"),
    };
}
