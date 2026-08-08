using System.Net;
using System.Net.Sockets;
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

    public static FakeSmtpScript Working() => new();

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

    readonly TcpListener listener;
    readonly Task session;
    readonly CancellationTokenSource cancellation = new();
    readonly List<string> commands = [];
    readonly FakeSmtpScript script;

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
            lock (commands)
                return [.. commands];
        }
    }

    public string? DataReceived { get; private set; }

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
            session.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // A cancelled or reset session is the normal way this server ends.
        }

        cancellation.Dispose();
    }

    async Task RunAsync()
    {
        using var tcp = await listener.AcceptTcpClientAsync(cancellation.Token);
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, NoBomUtf8);
        await using var writer = new StreamWriter(stream, NoBomUtf8) { AutoFlush = true, NewLine = "\r\n" };

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
            lock (commands)
                commands.Add(line);

            if (Is(line, "EHLO") || Is(line, "HELO"))
            {
                foreach (var ehloLine in script.EhloLines)
                    await writer.WriteLineAsync(ehloLine);
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
                DataReceived = await ReadDataAsync(reader);
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
            lock (commands)
                commands.Add(line);
        }
    }

    async Task<string> ReadDataAsync(StreamReader reader)
    {
        var body = new StringBuilder();

        while (await reader.ReadLineAsync(cancellation.Token) is { } line && line != ".")
            body.AppendLine(line);

        return body.ToString();
    }

    static bool Is(string line, string command) =>
        line.StartsWith(command, StringComparison.OrdinalIgnoreCase);
}
