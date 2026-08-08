using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MailTester.Tests.Fakes;

/// <summary>
/// What a fake server answers to each command. PIPELINING is deliberately never advertised:
/// with it MailKit sends MAIL FROM, RCPT TO and DATA in a single write, and parsing queued
/// commands would add complexity without testing anything extra.
/// </summary>
internal sealed record FakeSmtpScript
{
    public string? Greeting { get; init; } = "220 fake.local ESMTP FakeServer";

    public IReadOnlyList<string> EhloLines { get; init; } =
    [
        "250-fake.local",
        "250-SIZE 35882577",
        "250-AUTH PLAIN LOGIN",
        "250 8BITMIME",
    ];

    public string AuthResponse { get; init; } = "235 2.7.0 Authentication successful";

    public string MailFromResponse { get; init; } = "250 2.1.0 Ok";

    public string RcptToResponse { get; init; } = "250 2.1.5 Ok";

    public string DataAcceptedResponse { get; init; } = "250 2.0.0 Ok: queued as 2A9F1B0C3D";

    /// <summary>Close the connection right after greeting, without answering the EHLO.</summary>
    public bool DropAfterGreeting { get; init; }

    /// <summary>Answer with something that is not SMTP at all.</summary>
    public string? RawGarbage { get; init; }

    /// <summary>
    /// When true, STARTTLS is answered with 220 and the connection is upgraded to TLS in
    /// place; the client's second EHLO, sent over the now-encrypted stream, is read and
    /// answered exactly like the first.
    /// </summary>
    public bool OffersStartTls { get; init; }

    /// <summary>
    /// When true, the connection is wrapped in TLS immediately on accept, before the greeting
    /// is written -- the way an implicit-TLS (SMTPS) server behaves.
    /// </summary>
    public bool ImplicitTls { get; init; }

    public static FakeSmtpScript Working() => new();

    public static FakeSmtpScript WithStartTls() => new()
    {
        EhloLines =
        [
            "250-fake.local",
            "250-SIZE 35882577",
            "250-AUTH PLAIN LOGIN",
            "250-STARTTLS",
            "250 8BITMIME",
        ],
        OffersStartTls = true,
    };

    public static FakeSmtpScript WithImplicitTls() => new() { ImplicitTls = true };

    /// <summary>Accepts the connection and then says nothing, like a firewall holding the socket open.</summary>
    public static FakeSmtpScript Silent() => new() { Greeting = null };

    public static FakeSmtpScript NotSmtp() => new()
    {
        Greeting = null,
        RawGarbage = "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n",
    };

    public static FakeSmtpScript RejectsAuth() => new()
    {
        AuthResponse = "535 5.7.8 Error: authentication failed",
    };

    public static FakeSmtpScript RejectsSender() => new()
    {
        MailFromResponse = "550 5.7.1 Sender address rejected: not owned by user",
    };

    /// <summary>
    /// Same as Working(): the default EHLO never advertises STARTTLS. Named separately because a
    /// test asserting "requiring STARTTLS fails" should say so at the call site rather than lean
    /// on the reader knowing what Working() leaves out.
    /// </summary>
    public static FakeSmtpScript WithoutStartTls() => Working();
}

/// <summary>
/// A single-connection SMTP server on an ephemeral loopback port. Each test starts its own,
/// so there is no shared state and no hardcoded port.
/// </summary>
internal sealed class FakeSmtpServer : IDisposable
{
    // Encoding.UTF8 writes a byte-order-mark preamble on the stream's first write, which would
    // land in front of the greeting line and break the client's status-code parser.
    static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The certificate every TLS-capable script presents. Built once and shared: an
    /// EC key pair costs nothing to reuse, and nothing here ever mutates it. Public so a test
    /// can confirm the certificate an attempt captured is the one this fake actually sent.</summary>
    public static readonly X509Certificate2 Certificate = CreateCertificate();

    readonly TcpListener listener;
    readonly Task session;
    readonly CancellationTokenSource cancellation = new();
    readonly object sync = new();
    readonly List<string> commands = [];
    readonly FakeSmtpScript script;
    string? dataReceived;

    FakeSmtpServer(FakeSmtpScript script, TcpListener listener)
    {
        this.script = script;
        this.listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        session = Task.Run(RunAsync);
    }

    public int Port { get; }

    public IReadOnlyList<string> CommandsReceived
    {
        get
        {
            lock (sync)
                return [.. commands];
        }
    }

    public string? DataReceived
    {
        get
        {
            lock (sync)
                return dataReceived;
        }
    }

    public static FakeSmtpServer Start(FakeSmtpScript script)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FakeSmtpServer(script, listener);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        listener.Stop();

        try
        {
            // This is test infrastructure: a session that does not stop within the grace period
            // is a bug in the fake, not something to swallow silently behind a slow test.
            if (!session.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("FakeSmtpServer session did not stop within 5 seconds of cancellation.");
        }
        catch (AggregateException ex)
        {
            // A cancelled or reset session is the normal way this server ends. Anything else
            // is a real fault in the session loop and must not disappear silently.
            var faults = ex.Flatten().InnerExceptions
                .Where(inner => inner is not OperationCanceledException)
                .ToArray();

            if (faults.Length > 0)
                throw new AggregateException(faults);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    async Task RunAsync()
    {
        using var tcp = await listener.AcceptTcpClientAsync(cancellation.Token);
        var raw = tcp.GetStream();
        SslStream? tls = null;

        try
        {
            Stream stream;
            if (script.ImplicitTls)
                stream = tls = await UpgradeAsync(raw);
            else
                stream = raw;

            var reader = new StreamReader(stream, NoBomUtf8);
            var writer = new StreamWriter(stream, NoBomUtf8) { AutoFlush = true, NewLine = "\r\n" };

            if (script.RawGarbage is { } garbage)
            {
                await writer.WriteAsync(garbage);
                return;
            }

            if (script.Greeting is null)
            {
                // Hold the socket open and say nothing, so the client hits its timeout.
                await Task.Delay(Timeout.Infinite, cancellation.Token);
                return;
            }

            await writer.WriteLineAsync(script.Greeting);

            if (script.DropAfterGreeting)
                return;

            while (await reader.ReadLineAsync(cancellation.Token) is { } line)
            {
                lock (sync)
                    commands.Add(line);

                if (Is(line, "EHLO") || Is(line, "HELO"))
                {
                    foreach (var ehloLine in script.EhloLines)
                        await writer.WriteLineAsync(ehloLine);
                }
                else if (script.OffersStartTls && tls is null && Is(line, "STARTTLS"))
                {
                    await writer.WriteLineAsync("220 2.0.0 Ready to start TLS");
                    stream = tls = await UpgradeAsync(raw);

                    // The client re-reads capabilities with a second EHLO sent over the now
                    // encrypted channel; the loop picks it up through the swapped reader on its
                    // next iteration, exactly like the first one.
                    reader = new StreamReader(stream, NoBomUtf8);
                    writer = new StreamWriter(stream, NoBomUtf8) { AutoFlush = true, NewLine = "\r\n" };
                }
                else if (Is(line, "AUTH"))
                {
                    await HandleAuthAsync(line, reader, writer);
                }
                else if (Is(line, "MAIL FROM"))
                {
                    await writer.WriteLineAsync(script.MailFromResponse);
                }
                else if (Is(line, "RCPT TO"))
                {
                    await writer.WriteLineAsync(script.RcptToResponse);
                }
                else if (Is(line, "DATA"))
                {
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    var data = await ReadDataAsync(reader);
                    lock (sync)
                        dataReceived = data;
                    await writer.WriteLineAsync(script.DataAcceptedResponse);
                }
                else if (Is(line, "QUIT"))
                {
                    await writer.WriteLineAsync("221 2.0.0 Bye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("502 5.5.2 Unrecognized command");
                }
            }
        }
        catch (IOException)
        {
            // The client hung up without a graceful close -- most commonly here because a client
            // attempting implicit TLS reads this fake's plaintext greeting as a bogus TLS record
            // and aborts the connection. Unread bytes sitting in the socket at that point make
            // the OS send a reset instead of a clean FIN, which surfaces here as a write or read
            // failure. That is the client's doing, not a fault in this fake.
        }
        finally
        {
            // Disposing the TLS layer here, ahead of the outer "using var tcp", flushes and tears
            // down the SslStream before the raw socket underneath it goes away.
            tls?.Dispose();
        }
    }

    static async Task<SslStream> UpgradeAsync(Stream inner)
    {
        var tls = new SslStream(inner, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(Certificate, clientCertificateRequired: false, checkCertificateRevocation: false);
        return tls;
    }

    static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=fake.local", key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("fake.local");
        san.AddDnsName("127.0.0.1");
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.Now;
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

        // SslStream.AuthenticateAsServerAsync goes through SChannel on Windows, which rejects a
        // certificate whose private key is still the ephemeral one CreateSelfSigned attaches to
        // it, failing with "the credentials supplied to the package were not recognized".
        // Round-tripping the certificate through a PFX puts the key into a form SChannel accepts.
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    async Task HandleAuthAsync(string line, StreamReader reader, StreamWriter writer)
    {
        // LOGIN and CRAM-MD5 need a challenge round trip before the final status.
        if (Is(line, "AUTH LOGIN"))
        {
            await writer.WriteLineAsync("334 VXNlcm5hbWU6");
            await ReadAndRecordAsync(reader);
            await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
            await ReadAndRecordAsync(reader);
        }
        else if (Is(line, "AUTH CRAM-MD5"))
        {
            await writer.WriteLineAsync("334 PDEyMzQ1Njc4OUBmYWtlLmxvY2FsPg==");
            await ReadAndRecordAsync(reader);
        }

        await writer.WriteLineAsync(script.AuthResponse);
    }

    async Task ReadAndRecordAsync(StreamReader reader)
    {
        if (await reader.ReadLineAsync(cancellation.Token) is { } line)
        {
            lock (sync)
                commands.Add(line);
        }
    }

    async Task<string> ReadDataAsync(StreamReader reader)
    {
        var body = new StringBuilder();

        while (await reader.ReadLineAsync(cancellation.Token) is { } line && line != ".")
        {
            // RFC 5321 dot-stuffing: a sender prefixes any line that starts with a period with
            // one extra period, so the wire form of a content line beginning with "." always has
            // two or more leading periods. Undo that by dropping exactly one leading period; the
            // sole "." terminator line itself is already excluded by the loop condition above.
            body.AppendLine(line.StartsWith('.') ? line[1..] : line);
        }

        return body.ToString();
    }

    static bool Is(string line, string command) =>
        line.StartsWith(command, StringComparison.OrdinalIgnoreCase);
}
