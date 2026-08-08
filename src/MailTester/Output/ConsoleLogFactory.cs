using System.Diagnostics;
using MailTester.Cli;

namespace MailTester.Output;

internal static class ConsoleLogFactory
{
    /// <summary>
    /// Builds the log for a run. A --log-file that cannot be opened is reported through
    /// <paramref name="logFileWarning"/> and the run continues without it: giving up on the
    /// diagnosis because the log file failed would defeat the purpose of the tool.
    /// </summary>
    public static ConsoleLog Create(CliOptions options, TextWriter output, out string? logFileWarning)
    {
        logFileWarning = null;
        TextWriter? file = null;

        if (options.LogFile is { } path)
        {
            try
            {
                file = new StreamWriter(path, append: false) { AutoFlush = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                logFileWarning = $"No se pudo abrir --log-file '{path}': {ex.Message}. El diagnóstico sigue, sin archivo.";
            }
        }

        var stopwatch = Stopwatch.StartNew();
        return new ConsoleLog(output, file, PickColorizer(options), () => stopwatch.Elapsed);
    }

    static IColorizer PickColorizer(CliOptions options)
    {
        var suppressed = options.NoColor
                         || Console.IsOutputRedirected
                         || Environment.GetEnvironmentVariable("NO_COLOR") is not null;

        return suppressed ? new NullColorizer() : new ConsoleColorizer();
    }
}
