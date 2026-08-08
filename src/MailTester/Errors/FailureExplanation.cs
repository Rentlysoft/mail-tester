using MailTester.Smtp;

namespace MailTester.Errors;

/// <summary>
/// A failure rendered for a human: what broke, why it probably broke, and what to do next.
/// An untranslated exception reaching the user is a defect in this tool.
/// </summary>
internal sealed record FailureExplanation(
    string Title,
    AttemptPhase Phase,
    ExitCode ExitCode,
    string ProbableCause,
    IReadOnlyList<string> WhatToTry,
    string TechnicalDetail);
