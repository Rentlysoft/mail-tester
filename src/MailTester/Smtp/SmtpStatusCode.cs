namespace MailTester.Smtp;

/// <summary>
/// Reads the three-digit status code every SMTP server response line starts with.
///
/// Parsed digit by digit rather than with int.TryParse: TryParse's default number styles accept
/// leading whitespace and a leading sign, so it would misread a line like " 250" or "-334" as a
/// valid code. A status code is exactly three ASCII digits at the start of the line — nothing
/// else counts, and nothing else is checked.
/// </summary>
internal static class SmtpStatusCode
{
    public static bool TryParse(string line, out int code)
    {
        code = 0;

        if (line.Length < 3)
            return false;

        for (var i = 0; i < 3; i++)
        {
            var c = line[i];
            if (c < '0' || c > '9')
            {
                code = 0;
                return false;
            }

            code = code * 10 + (c - '0');
        }

        return true;
    }
}
