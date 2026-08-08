namespace MailTester.Errors;

/// <summary>
/// Process exit codes. Each class of failure gets its own code so that a script can
/// tell a firewall problem from a bad password without parsing the output.
/// </summary>
internal enum ExitCode
{
    Success = 0,
    Unexpected = 1,
    InvalidArguments = 2,
    NetworkFailure = 3,
    TlsFailure = 4,
    AuthenticationFailure = 5,
    SmtpRejected = 6,
    Timeout = 7,
}
