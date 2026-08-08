namespace MailTester.Output;

/// <summary>
/// Colour is applied through the console rather than with ANSI escapes: escapes are
/// unreliable on older Windows consoles and would leak into log files and test assertions.
/// </summary>
internal interface IColorizer
{
    void Set(ConsoleColor color);

    void Reset();
}

internal sealed class ConsoleColorizer : IColorizer
{
    public void Set(ConsoleColor color) => Console.ForegroundColor = color;

    public void Reset() => Console.ResetColor();
}

internal sealed class NullColorizer : IColorizer
{
    public void Set(ConsoleColor color)
    {
    }

    public void Reset()
    {
    }
}
