using MimeKit;

namespace MailTester.Cli;

internal static class ArgParser
{
    const string HelpHint = "Corré 'mail-tester --help' para ver el uso.";

    public static ParseResult Parse(string[] args)
    {
        if (args.Length == 0)
            return ParseResult.Failed([$"No se pasó ningún argumento. {HelpHint}"]);

        var errors = new List<string>();
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var recipients = new List<string>();
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;

        // Set only right after a token has been named as an unknown flag, and consumed (reset to
        // false) by the very next token regardless of what that token turns out to be. This
        // keeps the effect from reaching past the one token immediately following it.
        var previousTokenWasNamedUnknownFlag = false;

        while (i < args.Length)
        {
            var (flag, inline) = SplitFlag(args[i]);
            var followsNamedUnknownFlag = previousTokenWasNamedUnknownFlag;
            previousTokenWasNamedUnknownFlag = false;

            switch (flag)
            {
                case "--help":
                case "-h":
                    return ParseResult.Help();

                case "--allow-invalid-cert":
                case "--probe":
                case "--show-secrets":
                case "--no-color":
                    flags.Add(flag);
                    break;

                case "--to":
                    if (TakeValue(flag, inline) is { } recipient)
                        recipients.Add(recipient);
                    break;

                case "--host":
                case "--port":
                case "--security":
                case "--auth":
                case "--user":
                case "--pass":
                case "--from":
                case "--subject":
                case "--body":
                case "--ehlo-domain":
                case "--timeout":
                case "--log-file":
                    if (TakeValue(flag, inline) is { } value && !scalars.TryAdd(flag, value))
                        errors.Add($"El argumento '{flag}' fue pasado más de una vez.");
                    break;

                default:
                    // A token that does not start with '-' at all is not a misspelled flag: it is
                    // almost certainly a value stranded by an unrecognised flag right before it
                    // (a typo like "--passs" leaves its intended value, e.g. a password, looking
                    // like this). The same is true of a token that does start with '-' when it
                    // immediately follows a flag that was just named as unknown: it reads exactly
                    // like a second, independent typo, but is far more likely that flag's
                    // intended value (a typo like "--passs -hunter2" leaves the password looking
                    // like a second unknown flag). Echoing either would be the one way a
                    // credential could reach stderr without --show-secrets, so neither is ever
                    // printed, not even truncated -- only reported by position.
                    //
                    // A token that starts with '-' and does NOT follow a freshly named unknown
                    // flag is reported by its pre-'=' prefix only, so "--pas=hunter2" never echoes
                    // the value after the '=', and that naming is what makes the *next* token
                    // eligible for this same suppression.
                    if (!args[i].StartsWith('-') || followsNamedUnknownFlag)
                        errors.Add($"Se pasó un valor en la posición {i + 1} sin un flag que lo preceda. {HelpHint}");
                    else
                    {
                        errors.Add($"Argumento desconocido: '{flag}'. {HelpHint}");
                        previousTokenWasNamedUnknownFlag = true;
                    }
                    break;
            }

            i++;
        }

        var probe = flags.Contains("--probe");

        var host = scalars.GetValueOrDefault("--host");
        if (string.IsNullOrWhiteSpace(host))
            errors.Add("Falta --host: el hostname o la IP del servidor SMTP.");

        var port = 587;
        var portSpecified = false;
        if (scalars.TryGetValue("--port", out var portText))
        {
            if (!int.TryParse(portText, out var parsedPort) || parsedPort is < 1 or > 65535)
                errors.Add($"--port tiene que ser un número entre 1 y 65535, no '{portText}'.");
            else
                (port, portSpecified) = (parsedPort, true);
        }

        var security = SecurityMode.Auto;
        var securitySpecified = false;
        if (scalars.TryGetValue("--security", out var securityText))
        {
            if (!SecurityModes.TryParse(securityText, out var parsedSecurity))
                errors.Add($"--security no acepta '{securityText}'. Valores válidos: {SecurityModes.ValidValues}.");
            else
                (security, securitySpecified) = (parsedSecurity, true);
        }

        var auth = AuthMechanism.Auto;
        if (scalars.TryGetValue("--auth", out var authText))
        {
            if (!AuthMechanisms.TryParse(authText, out var parsedAuth))
                errors.Add($"--auth no acepta '{authText}'. Valores válidos: {AuthMechanisms.ValidValues}.");
            else
                auth = parsedAuth;
        }

        var timeoutSeconds = probe ? 10 : 30;
        if (scalars.TryGetValue("--timeout", out var timeoutText))
        {
            if (!int.TryParse(timeoutText, out var parsedTimeout) || parsedTimeout is < 1 or > 600)
                errors.Add($"--timeout tiene que ser un número de segundos entre 1 y 600, no '{timeoutText}'.");
            else
                timeoutSeconds = parsedTimeout;
        }

        var user = scalars.GetValueOrDefault("--user");
        var password = scalars.GetValueOrDefault("--pass");
        if (user is not null && password is null && auth != AuthMechanism.None)
            errors.Add("--user necesita --pass. Si el servidor no pide autenticación, usá --auth none.");
        if (password is not null && user is null)
            errors.Add("--pass sin --user no tiene sentido: agregá --user o quitá --pass.");

        MailboxAddress? from = null;
        if (scalars.GetValueOrDefault("--from") is { } fromText)
        {
            if (TryParseAddress(fromText, out var parsedFrom))
                from = parsedFrom;
            else
                errors.Add($"--from no es una dirección válida: '{fromText}'.");
        }
        else if (!probe)
        {
            errors.Add("Falta --from: la dirección del remitente.");
        }

        var to = new List<MailboxAddress>();
        foreach (var recipientText in recipients)
        {
            if (TryParseAddress(recipientText, out var parsedTo))
                to.Add(parsedTo);
            else
                errors.Add($"--to no es una dirección válida: '{recipientText}'.");
        }

        if (recipients.Count == 0 && !probe)
            errors.Add("Falta --to: al menos una dirección de destino.");

        if (probe)
        {
            foreach (var contentFlag in new[] { "--subject", "--body" })
            {
                if (scalars.ContainsKey(contentFlag))
                    errors.Add($"{contentFlag} no aplica con --probe, que no envía ningún mensaje.");
            }
        }

        if (errors.Count > 0)
            return ParseResult.Failed(errors);

        // host is non-null here: the only path that leaves it null already added an error above.
        return ParseResult.Parsed(new CliOptions
        {
            Host = host!,
            Port = port,
            PortSpecified = portSpecified,
            Security = security,
            SecuritySpecified = securitySpecified,
            Auth = auth,
            User = user,
            Password = password,
            From = from,
            To = to,
            Subject = scalars.GetValueOrDefault("--subject"),
            Body = scalars.GetValueOrDefault("--body"),
            EhloDomain = scalars.GetValueOrDefault("--ehlo-domain"),
            TimeoutSeconds = timeoutSeconds,
            AllowInvalidCert = flags.Contains("--allow-invalid-cert"),
            Probe = probe,
            LogFile = scalars.GetValueOrDefault("--log-file"),
            ShowSecrets = flags.Contains("--show-secrets"),
            NoColor = flags.Contains("--no-color"),
        });

        string? TakeValue(string flag, string? inline)
        {
            if (inline is not null)
                return inline;

            if (i + 1 >= args.Length)
            {
                errors.Add($"El argumento '{flag}' necesita un valor.");
                return null;
            }

            return args[++i];
        }
    }

    /// <summary>Splits "--flag=value" on the first '=' so that values may contain '=' themselves.</summary>
    static (string Flag, string? InlineValue) SplitFlag(string arg)
    {
        var separator = arg.IndexOf('=');
        return separator < 0 ? (arg, null) : (arg[..separator], arg[(separator + 1)..]);
    }

    /// <summary>
    /// MimeKit's mailbox grammar tolerates a local part with no domain (e.g. "nope"), which is
    /// not a usable SMTP address, so a domain is required on top of MailboxAddress.TryParse.
    /// </summary>
    static bool TryParseAddress(string text, out MailboxAddress address)
    {
        if (MailboxAddress.TryParse(text, out var parsed) && parsed.Address.Contains('@'))
        {
            address = parsed;
            return true;
        }

        address = null!;
        return false;
    }
}
