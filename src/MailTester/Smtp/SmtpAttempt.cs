using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailTester.Cli;
using MailTester.Messages;
using MailTester.Output;
using MimeKit;

namespace MailTester.Smtp;

/// <summary>
/// One attempt against one port and one security mode, narrated step by step.
///
/// DNS and the TCP connect happen here instead of inside MailKit so that each can be timed and
/// attributed on its own; MailKit is then handed the already connected socket. Single use:
/// the instance accumulates the attempt's state, so probe mode creates one per combination.
/// </summary>
internal sealed class SmtpAttempt(CliOptions options, ConsoleLog log)
{
    const int TotalSteps = 6;

    readonly Stopwatch clock = new();
    readonly Dictionary<AttemptPhase, TimeSpan> timings = [];

    AttemptPhase phase = AttemptPhase.Dns;
    TimeSpan phaseStartedAt = TimeSpan.Zero;
    IReadOnlyList<IPAddress> resolvedAddresses = [];
    IPAddress? connectedAddress;
    string? localEndPoint;
    IReadOnlyList<string> capabilities = [];
    IReadOnlyList<string> offeredMechanisms = [];
    string? authMechanismUsed;
    bool authenticated;
    bool messageSent;
    string? serverResponse;
    string? messageId;
    bool secure;
    SslProtocols? tlsProtocol;
    string? cipherSuite;
    bool used;
    bool connectionFactsCaptured;

    public async Task<AttemptResult> RunAsync(
        int port,
        SecurityMode security,
        bool sendMessage,
        CancellationToken cancellationToken)
    {
        if (used)
            throw new InvalidOperationException("SmtpAttempt es de un solo uso; hay que crear una instancia nueva por intento.");
        used = true;

        clock.Restart();

        var inspector = new CertificateInspector(log, options.Host, options.AllowInvalidCert, onAccepted: () =>
        {
            if (PhaseAfterCertificateAccepted(security) is { } next)
                EnterPhase(next);
        });
        var logger = new SmtpProtocolLogger(
            log,
            new SecretRedactor(options.ShowSecrets),
            new PhaseDetector(),
            EnterPhase);

        using var client = new SmtpClient(logger);
        // MailKit's own socket-level timeout gets clear headroom over our deadline below, so our
        // linked token always expires first. Without it the two race, and roughly nine times out
        // of ten MailKit wins: it throws a plain, unattributed System.TimeoutException in English,
        // instead of the Spanish, phase-attributed one this tool is built to produce.
        client.Timeout = (int)(options.Timeout.TotalMilliseconds * 2);
        client.LocalDomain = options.EhloDomain ?? Environment.MachineName;
        client.ServerCertificateValidationCallback = inspector.Validate;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        var token = timeout.Token;

        Socket? socket = null;

        try
        {
            var startedAt = clock.Elapsed;
            Step(1, $"Resolviendo DNS de {options.Host}");
            resolvedAddresses = await ResolveAsync(token);
            Ok($"{string.Join(", ", resolvedAddresses)}", startedAt);

            EnterPhase(AttemptPhase.TcpConnect);
            startedAt = clock.Elapsed;
            Step(2, $"Conectando TCP a {options.Host}:{port} (presupuesto total del intento: {options.TimeoutSeconds}s)");
            socket = await ConnectAsync(resolvedAddresses, port, token);
            connectedAddress = (socket.RemoteEndPoint as IPEndPoint)?.Address;
            localEndPoint = socket.LocalEndPoint?.ToString();
            Ok($"conectado a {connectedAddress}:{port} desde {localEndPoint}", startedAt);

            // With implicit TLS the handshake precedes the greeting, and there is no STARTTLS
            // command for the phase detector to see.
            EnterPhase(security == SecurityMode.Ssl ? AttemptPhase.TlsHandshake : AttemptPhase.Greeting);
            startedAt = clock.Elapsed;
            Step(3, $"Handshake SMTP: saludo, EHLO y TLS ({security.ToCliName()})");
            await client.ConnectAsync(socket, options.Host, port, security.ToSocketOptions(), token);
            CaptureConnectionFacts(client);
            if (capabilities.Count > 0)
                log.Line(LogLevel.Caps, string.Join(" · ", capabilities));
            Ok(secure ? $"handshake completo · {tlsProtocol} · {cipherSuite}" : "handshake completo · sin cifrado", startedAt);

            if (options.ShouldAuthenticate)
            {
                EnterPhase(AttemptPhase.Authenticate);
                startedAt = clock.Elapsed;
                authMechanismUsed = ForceMechanism(client, out var advertised);
                Step(4, $"Autenticando como {options.User} (mecanismo: {authMechanismUsed ?? "negociado por MailKit"})");
                if (authMechanismUsed is { } forced && !advertised)
                    log.Line(LogLevel.Warn, $"El servidor no anunció {forced}; se intenta igual para ver qué responde.");
                await AuthenticateAsync(client, token);
                authenticated = true;
                authMechanismUsed ??= "negociado por MailKit";
                Ok($"autenticado como {options.User}", startedAt);
            }
            else
            {
                Skipped(4, "AUTH", options.User is null ? "sin credenciales" : "--auth none");
            }

            if (sendMessage)
            {
                EnterPhase(AttemptPhase.Send);
                startedAt = clock.Elapsed;
                var message = BuildMessage(port, security);
                messageId = message.MessageId;
                Step(5, $"Enviando mensaje a {options.To.Count} destinatario(s) · Message-Id {messageId}");
                serverResponse = await client.SendAsync(message, token);
                messageSent = true;
                log.Line(LogLevel.Sent, $"aceptado: {serverResponse}  ({Millis(startedAt)} ms)");
            }
            else
            {
                Skipped(5, "SEND", "--probe no envía mensajes");
            }

            EnterPhase(AttemptPhase.Quit);
            startedAt = clock.Elapsed;
            Step(6, "Cerrando la sesión (QUIT)");
            await client.DisconnectAsync(true, token);
            Ok("sesión cerrada", startedAt);

            return Build(port, security, inspector, exception: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own deadline fired. Surfacing it as a timeout keeps the explainer from having
            // to tell a user's Ctrl+C apart from an expired budget.
            var expired = new TimeoutException($"La operación superó el timeout de {options.TimeoutSeconds} s en la fase {phase}.");
            CaptureConnectionFacts(client);
            return Build(port, security, inspector, expired);
        }
        catch (Exception ex)
        {
            CaptureConnectionFacts(client);
            return Build(port, security, inspector, ex);
        }
        finally
        {
            // MailKit wraps the socket in a NetworkStream(ownsSocket: true) the moment
            // ConnectAsync is called, so the socket is already disposed one way or another by
            // the time this runs, whether or not the handshake succeeded. Disposing it again is
            // harmless because Socket.Dispose() is idempotent; this exists so the cleanup stays
            // obviously correct even if that internal MailKit detail ever changed.
            if (socket is not null && !client.IsConnected)
                socket.Dispose();

            logger.Dispose();
        }
    }

    async Task<IReadOnlyList<IPAddress>> ResolveAsync(CancellationToken token)
    {
        if (IPAddress.TryParse(options.Host, out var literal))
        {
            log.Line(LogLevel.Info, "El host es una IP literal: no hay resolución DNS que hacer.");
            return [literal];
        }

        var addresses = await Dns.GetHostAddressesAsync(options.Host, token);

        return addresses.Length > 0
            ? addresses
            : throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Tries every resolved address in order, the way a real client does.</summary>
    async Task<Socket> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken token)
    {
        Exception? last = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                await socket.ConnectAsync(address, port, token);
                return socket;
            }
            catch (OperationCanceledException)
            {
                // The budget ran out mid-dial. Reporting this as "could not connect" and moving
                // on to the next address would be a lie: none of the remaining addresses were
                // ever dialled, and an already-cancelled token would make every one of them fail
                // instantly, printing a false warning for each.
                socket.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;

                if (addresses.Count > 1)
                    log.Line(LogLevel.Warn, $"No se pudo conectar a {address}:{port} — {ex.Message}");
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    async Task AuthenticateAsync(SmtpClient client, CancellationToken token)
    {
        var user = options.User ?? throw new InvalidOperationException("Se intentó autenticar sin usuario.");
        var password = options.Password ?? throw new InvalidOperationException("Se intentó autenticar sin password.");

        await client.AuthenticateAsync(user, password, token);
    }

    /// <summary>
    /// Restricts MailKit to a single mechanism. A mechanism the server never advertised is still
    /// attempted: what the server answers is information, not a reason to refuse locally. Logging
    /// is left to the caller, so the warning prints after the step it belongs to rather than
    /// before it.
    /// </summary>
    string? ForceMechanism(SmtpClient client, out bool advertised)
    {
        advertised = true;

        if (options.Auth.ToSaslName() is not { } mechanism)
            return null;

        advertised = client.AuthenticationMechanisms.Contains(mechanism);
        client.AuthenticationMechanisms.Clear();
        client.AuthenticationMechanisms.Add(mechanism);

        return mechanism;
    }

    /// <summary>
    /// Reads back what MailKit already knows about the connection so far. Called on every exit
    /// path, not just success: MailKit parses the EHLO response before it attempts STARTTLS, so
    /// a handshake that fails after a successful EHLO still leaves capabilities and offered
    /// mechanisms sitting on the client, and a failed attempt that never reports them would look
    /// like the EHLO itself never happened.
    ///
    /// Captures only once per attempt: ForceMechanism narrows client.AuthenticationMechanisms
    /// down to a single forced mechanism before authenticating, and a second capture taken after
    /// that -- from a catch block following a failed forced auth -- would read that narrowed
    /// collection instead of what the server actually announced in its EHLO, corrupting both
    /// AuthMechanismsOffered and the AUTH= entry in Capabilities.
    /// </summary>
    void CaptureConnectionFacts(SmtpClient client)
    {
        if (connectionFactsCaptured)
            return;
        connectionFactsCaptured = true;

        secure = client.IsSecure;
        tlsProtocol = secure ? client.SslProtocol : null;
        cipherSuite = secure
            ? client.SslCipherSuite?.ToString() ?? $"{client.SslCipherAlgorithm} ({client.SslCipherStrength} bits)"
            : null;

        offeredMechanisms = [.. client.AuthenticationMechanisms.OrderBy(m => m, StringComparer.Ordinal)];

        var items = new List<string>();

        foreach (var capability in Enum.GetValues<SmtpCapabilities>())
        {
            if (capability != SmtpCapabilities.None && client.Capabilities.HasFlag(capability))
                items.Add(capability.ToString().ToUpperInvariant());
        }

        if (client.MaxSize > 0)
            items.Add($"SIZE={client.MaxSize}");

        if (offeredMechanisms.Count > 0)
            items.Add($"AUTH={string.Join(" ", offeredMechanisms)}");

        capabilities = items;
    }

    MimeMessage BuildMessage(int port, SecurityMode security)
    {
        var auth = options.ShouldAuthenticate
            ? $"{authMechanismUsed ?? "negociado"} como {options.User}"
            : "sin autenticación";

        return TestMessageFactory.Create(options, new MessageContext(
            DateTimeOffset.Now,
            $"{options.Host}:{port} ({security.ToCliName()})",
            auth,
            $"{Environment.MachineName} ({localEndPoint})"));
    }

    AttemptResult Build(int port, SecurityMode security, CertificateInspector inspector, Exception? exception)
    {
        EnterPhase(phase);

        return new AttemptResult
        {
            Success = exception is null,
            Port = port,
            Security = security,
            LastPhase = phase,
            FailedPhase = exception is null ? null : Attribute(exception),
            Exception = exception,
            ResolvedAddresses = resolvedAddresses,
            ConnectedAddress = connectedAddress,
            LocalEndPoint = localEndPoint,
            Secure = secure,
            TlsProtocol = tlsProtocol,
            CipherSuite = cipherSuite,
            ServerCertificate = inspector.ServerCertificate,
            CertificateErrors = inspector.Errors,
            Capabilities = capabilities,
            AuthMechanismsOffered = offeredMechanisms,
            AuthMechanismUsed = authMechanismUsed,
            Authenticated = authenticated,
            MessageSent = messageSent,
            ServerResponse = serverResponse,
            MessageId = messageId,
            Total = clock.Elapsed,
            PhaseTimings = new Dictionary<AttemptPhase, TimeSpan>(timings),
        };
    }

    /// <summary>
    /// Implicit TLS has no STARTTLS command for PhaseDetector to observe, so an accepted
    /// certificate is the signal that the handshake got far enough that a greeting is the very
    /// next thing expected. Under STARTTLS the certificate is validated mid-EHLO, and what
    /// follows is the second EHLO, not a greeting -- MailKit re-issues it immediately once the
    /// handshake succeeds, and PhaseDetector picks that up on its own, so advancing here too
    /// would only race that write for no benefit, and would misattribute anything that failed
    /// in between as a greeting timeout instead of a TLS one.
    /// </summary>
    internal static AttemptPhase? PhaseAfterCertificateAccepted(SecurityMode security) =>
        security == SecurityMode.Ssl ? AttemptPhase.Greeting : null;

    /// <summary>
    /// The phase read off the wire, overridden where the exception type knows better: a TLS
    /// handshake failure produces no protocol lines at all, and requiring STARTTLS against a
    /// server that never advertised it fails locally, before any TLS record is exchanged, so the
    /// phase detector never sees it either.
    ///
    /// MailKit throws NotSupportedException for two unrelated reasons: STARTTLS was required but
    /// never advertised, and AUTH was required but the server advertised no usable mechanism.
    /// Only the first one is a TLS-layer fact; the guard keeps the second one attributed to the
    /// phase it actually failed in instead of being misdiagnosed as a TLS problem.
    /// </summary>
    AttemptPhase Attribute(Exception exception) => exception switch
    {
        SslHandshakeException => AttemptPhase.TlsHandshake,
        NotSupportedException when phase != AttemptPhase.Authenticate => AttemptPhase.TlsHandshake,
        // Fully qualified on purpose: System.Security.Authentication also defines an
        // AuthenticationException, and this file imports both namespaces.
        MailKit.Security.AuthenticationException => AttemptPhase.Authenticate,
        _ => phase,
    };

    void EnterPhase(AttemptPhase next)
    {
        var elapsed = clock.Elapsed;
        timings[phase] = timings.GetValueOrDefault(phase) + (elapsed - phaseStartedAt);
        phaseStartedAt = elapsed;
        phase = next;
    }

    void Step(int number, string message) => log.Line(LogLevel.Step, $"{number}/{TotalSteps} {message}");

    /// <summary>
    /// A step that does not apply is printed anyway, with its reason: silently skipping it
    /// leaves the reader wondering whether it was attempted and failed quietly.
    /// </summary>
    void Skipped(int number, string title, string reason) =>
        log.Line(LogLevel.Step, $"{number}/{TotalSteps} {title} — omitido ({reason})");

    void Ok(string message, TimeSpan startedAt) => log.Line(LogLevel.Ok, $"{message}  ({Millis(startedAt)} ms)");

    string Millis(TimeSpan startedAt) => (clock.Elapsed - startedAt).TotalMilliseconds.ToString("F0");
}
