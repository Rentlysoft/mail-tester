using MailTester.Smtp;

namespace MailTester.Errors;

/// <summary>
/// A failure rendered for a human: what broke, why it probably broke, and what to do next.
/// An untranslated exception reaching the user is a defect in this tool.
///
/// <paramref name="Interrupted"/> marks the one case that is not really a failure at all: a run
/// cut off by cancellation. FailureReport uses it to word its summary line accordingly, so the
/// same block never both says "this was not a failure" in the cause and "FALLA" in the summary.
/// </summary>
internal sealed record FailureExplanation(
    string Title,
    AttemptPhase Phase,
    ExitCode ExitCode,
    string ProbableCause,
    IReadOnlyList<string> WhatToTry,
    string TechnicalDetail,
    bool Interrupted = false);
