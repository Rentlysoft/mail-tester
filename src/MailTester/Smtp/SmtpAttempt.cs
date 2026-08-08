using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
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

    public async Task<AttemptResult> RunAsync(
        int port,
        SecurityMode security,
        bool sendMessage,
        CancellationToken cancellationToken)
    {
        clock.Restart();

        var inspector = new CertificateInspector(log, options.Host, options.AllowInvalidCert);
        var logger = new SmtpProtocolLogger(
            log,
            new SecretRedactor(options.ShowSecrets),
            new PhaseDetector(),
            EnterPhase);

        using var client = new SmtpClient(logger);
        client.Timeout = (int)options.Timeout.TotalMilliseconds;
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
            Step(2, $"Conectando TCP a {options.Host}:{port} (timeout {options.TimeoutSeconds}s)");
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
            log.Line(LogLevel.Caps, string.Join(" · ", capabilities));
            Ok(secure ? $"handshake completo · {tlsProtocol} · {cipherSuite}" : "handshake completo · sin cifrado", startedAt);

            if (options.ShouldAuthenticate)
            {
                EnterPhase(AttemptPhase.Authenticate);
                startedAt = clock.Elapsed;
                authMechanismUsed = ForceMechanism(client);
                Step(4, $"Autenticando como {options.User} (mecanismo: {authMechanismUsed ?? "negociado por MailKit"})");
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
            return Build(port, security, inspector, expired);
        }
        catch (Exception ex)
        {
            return Build(port, security, inspector, ex);
        }
        finally
        {
            // MailKit owns the socket once it connects; if it never did, it is ours to close.
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
    /// attempted: what the server answers is information, not a reason to refuse locally.
    /// </summary>
    string? ForceMechanism(SmtpClient client)
    {
        if (options.Auth.ToSaslName() is not { } mechanism)
            return null;

        var advertised = client.AuthenticationMechanisms.Contains(mechanism);
        client.AuthenticationMechanisms.Clear();
        client.AuthenticationMechanisms.Add(mechanism);

        if (!advertised)
            log.Line(LogLevel.Warn, $"El servidor no anunció {mechanism}; se intenta igual para ver qué responde.");

        return mechanism;
    }

    void CaptureConnectionFacts(SmtpClient client)
    {
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
            PhaseTimings = timings,
        };
    }

    /// <summary>
    /// The phase read off the wire, overridden where the exception type knows better: a TLS
    /// handshake failure produces no protocol lines at all.
    /// </summary>
    AttemptPhase Attribute(Exception exception) => exception switch
    {
        SslHandshakeException => AttemptPhase.TlsHandshake,
        // Fully qualified on purpose: System.Security.Authentication also defines an
        // AuthenticationException, and this file imports both namespaces.
        MailKit.Security.AuthenticationException => AttemptPhase.Authenticate,
        SocketException when phase == AttemptPhase.Dns => AttemptPhase.Dns,
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
