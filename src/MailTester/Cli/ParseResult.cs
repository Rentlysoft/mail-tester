namespace MailTester.Cli;

/// <summary>
/// Outcome of parsing the command line: exactly one of help, errors, or options.
/// </summary>
internal sealed record ParseResult
{
    public CliOptions? Options { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HelpRequested { get; init; }

    public static ParseResult Help() => new() { HelpRequested = true };

    public static ParseResult Failed(IReadOnlyList<string> errors) => new() { Errors = errors };

    public static ParseResult Parsed(CliOptions options) => new() { Options = options };
}
