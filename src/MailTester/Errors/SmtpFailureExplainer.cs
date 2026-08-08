using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailTester.Cli;
using MailTester.Smtp;

namespace MailTester.Errors;

internal static class SmtpFailureExplainer
{
    public static FailureExplanation Explain(AttemptResult result, CliOptions options)
    {
        var exception = result.Exception
                        ?? throw new InvalidOperationException("Se pidió explicar un intento que no falló.");
        var phase = result.FailedPhase ?? result.LastPhase;

        return exception switch
        {
            // Covers TaskCanceledException too: SmtpAttempt hands back its own cancellation as
            // an ordinary failed AttemptResult instead of throwing, so this is the only place
            // that tells the caller's Ctrl+C apart from an actual protocol or network failure.
            OperationCanceledException => FromCancellation(result, options, phase),
            SocketException socket => FromSocket(socket, result, options, phase),
            TimeoutException => FromTimeout(result, options, phase),
            SslHandshakeException => FromTls(result, options),
            NotSupportedException => FromNotSupported(result, options, phase),
            MailKit.Security.AuthenticationException => FromRejectedCredentials(result, options, exception),
            SmtpCommandException command => FromCommand(command, result, options, phase),
            SmtpProtocolException => FromProtocol(result, options, phase),
            _ => Unclassified(exception, phase),
        };
    }

    static FailureExplanation FromCancellation(AttemptResult result, CliOptions options, AttemptPhase phase) =>
        new(
            "DIAGNÓSTICO INTERRUMPIDO",
            phase,
            ExitCode.Unexpected,
            $"Interrumpido antes de terminar, en la fase {phase}: esto no es una falla del servidor ni de la configuración, es una cancelación (Ctrl+C u otra señal) que cortó el diagnóstico a mitad de camino.",
            [
                "Volver a correr el mismo comando: nada indica que vaya a fallar, solo se cortó antes de tiempo.",
                !options.Probe && result.MessageSent
                    ? "El mensaje ya se había enviado cuando llegó la interrupción: puede haber quedado encolado en el servidor, conviene revisar ahí antes de reenviarlo."
                    : "No llegó a enviarse ningún mensaje antes de la interrupción.",
            ],
            Describe(result.Exception!),
            Interrupted: true);

    static FailureExplanation FromSocket(SocketException exception, AttemptResult result, CliOptions options, AttemptPhase phase) =>
        exception.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => new(
                Title(AttemptPhase.Dns),
                AttemptPhase.Dns,
                ExitCode.NetworkFailure,
                $"El nombre '{options.Host}' no resuelve a ninguna dirección: el resolver del sistema dice que no existe.",
                [
                    $"Verificar el nombre: nslookup {options.Host}",
                    "Si el servidor es interno, confirmar que la máquina esté usando el DNS de esa red (VPN conectada, sufijo de búsqueda correcto).",
                    "Si conocés la IP, pasarla directamente en --host para separar el problema de DNS del de conectividad.",
                ],
                Describe(exception)),

            SocketError.ConnectionRefused => new(
                Title(AttemptPhase.TcpConnect),
                AttemptPhase.TcpConnect,
                ExitCode.NetworkFailure,
                $"Hay ruta hasta el host pero nada escuchando en el puerto {result.Port}: la máquina rechazó la conexión activamente. Un puerto rechazado no es un firewall, es un servicio que no está ahí.",
                [
                    $"mail-tester --probe --host {options.Host}  (barre los puertos habituales)",
                    "Confirmar que el servicio SMTP esté levantado en ese puerto.",
                    "Si estabas probando el 25, muchos proveedores de red y de cloud lo bloquean: probá 587.",
                ],
                Describe(exception)),

            SocketError.TimedOut => FromTimeout(result, options, phase),

            SocketError.NetworkUnreachable or SocketError.HostUnreachable => new(
                Title(AttemptPhase.TcpConnect),
                AttemptPhase.TcpConnect,
                ExitCode.NetworkFailure,
                "No hay ruta hasta el host: falló el enrutamiento, no el servidor SMTP.",
                [
                    "Verificar la VPN o el enrutamiento hacia esa red.",
                    $"Probar conectividad básica: ping {options.Host}",
                ],
                Describe(exception)),

            SocketError.ConnectionReset => new(
                Title(phase),
                phase,
                ExitCode.NetworkFailure,
                "El servidor cerró la conexión de golpe (reset). Suele pasar cuando el modo de TLS no es el que el puerto espera, cuando la IP de origen no está autorizada, o cuando hay rate limiting.",
                [
                    $"mail-tester --probe --host {options.Host}  (prueba todas las combinaciones de TLS)",
                    "Confirmar con el administrador si la IP de origen está permitida.",
                ],
                Describe(exception)),

            _ => new(
                Title(phase),
                phase,
                ExitCode.NetworkFailure,
                $"Falla de socket ({exception.SocketErrorCode}) en la fase {phase}.",
                [$"mail-tester --probe --host {options.Host}"],
                Describe(exception)),
        };

    static FailureExplanation FromTimeout(AttemptResult result, CliOptions options, AttemptPhase phase)
    {
        var (cause, suggestions) = phase switch
        {
            AttemptPhase.TcpConnect =>
            (
                $"El puerto {result.Port} no respondió: ni aceptó ni rechazó la conexión. Ese silencio es la firma de un firewall que descarta los paquetes; un servicio caído rechaza la conexión en vez de ignorarla.",
                new[]
                {
                    "Probar desde otra red para descartar el firewall local o el de la oficina.",
                    "Si la red es lenta pero funciona, subir --timeout.",
                    "Revisar reglas de salida, security groups o NSGs. El puerto 25 de salida está bloqueado por defecto en la mayoría de los clouds.",
                }
            ),

            AttemptPhase.Greeting =>
            (
                "El TCP se estableció pero el servidor nunca mandó su saludo 220. Típico de un middlebox o proxy que acepta la conexión y no la reenvía, o de un servidor con tarpitting que castiga clientes desconocidos.",
                new[]
                {
                    "Subir --timeout: algunos servidores demoran el saludo a propósito.",
                    "Verificar que el puerto sea realmente de SMTP y no esté detrás de un balanceador mal configurado.",
                    $"mail-tester --probe --host {options.Host}",
                }
            ),

            AttemptPhase.Authenticate =>
            (
                "El servidor aceptó el comando AUTH y no contestó dentro del timeout. Suele indicar un backend de autenticación lento (LDAP o Active Directory que no responde).",
                new[] { "Subir --timeout.", "Consultar con el administrador el estado del backend de autenticación." }
            ),

            AttemptPhase.Send =>
            (
                "El mensaje se transmitió y el servidor no confirmó dentro del timeout. Habitual cuando hay antivirus o antispam analizando el contenido antes de aceptar.",
                new[]
                {
                    "Subir --timeout a 60 o más.",
                    "Ojo: el mensaje puede haberse encolado igual. Buscar el Message-Id en los logs del servidor antes de reintentar.",
                }
            ),

            _ =>
            (
                $"La operación superó el timeout de {options.TimeoutSeconds} s en la fase {phase}.",
                new[] { "Subir --timeout.", $"mail-tester --probe --host {options.Host}" }
            ),
        };

        return new FailureExplanation(Title(phase), phase, ExitCode.Timeout, cause, suggestions, Describe(result.Exception!));
    }

    static FailureExplanation FromTls(AttemptResult result, CliOptions options)
    {
        const AttemptPhase tls = AttemptPhase.TlsHandshake;

        if (result.CertificateErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return new FailureExplanation(
                Title(tls), tls, ExitCode.TlsFailure,
                $"El certificado que presentó el servidor no cubre '{options.Host}'. La conexión funciona; lo que falla es la identidad.",
                [
                    "Usar en --host el nombre que figura en el certificado (está listado más arriba en las líneas CERT).",
                    "Si el nombre correcto no es alcanzable, --allow-invalid-cert permite seguir el diagnóstico sin verificar la identidad.",
                ],
                Describe(result.Exception!));
        }

        if (result.CertificateErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            return new FailureExplanation(
                Title(tls), tls, ExitCode.TlsFailure,
                "La cadena del certificado no valida. Las causas habituales son un certificado autofirmado, un intermedio que el servidor no manda, o un certificado expirado.",
                [
                    "Revisar las líneas CERT de arriba: vigencia e issuer dicen cuál de las tres es.",
                    "--allow-invalid-cert permite seguir el diagnóstico.",
                    "Para arreglarlo de verdad: instalar la CA en el almacén de confianza, o hacer que el servidor mande la cadena completa.",
                ],
                Describe(result.Exception!));
        }

        if (result.CertificateErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return new FailureExplanation(
                Title(tls), tls, ExitCode.TlsFailure,
                "El servidor no presentó certificado durante el handshake, así que no hay TLS posible.",
                [
                    $"Probar sin cifrado: --security none --port {result.Port}",
                    $"mail-tester --probe --host {options.Host}",
                ],
                Describe(result.Exception!));
        }

        // No certificate error: the mismatch is in how TLS was started, or in versions and ciphers.
        // Scoped to the ports where STARTTLS is the universal convention: a custom port doing
        // implicit TLS legitimately is indistinguishable from this case by port number alone, so
        // asserting "the server expects plaintext" for it would be a guess dressed up as a fact.
        if (result.Security == SecurityMode.Ssl && IsConventionalStartTlsPort(result.Port))
        {
            return new FailureExplanation(
                Title(tls), tls, ExitCode.TlsFailure,
                $"Pediste --security ssl (TLS implícito) contra el puerto {result.Port}, que normalmente espera STARTTLS: TLS explícito sobre una conexión que arranca en claro. El servidor está esperando un EHLO en texto plano mientras el cliente manda un ClientHello.",
                [
                    "--port 587 --security starttls   (submission estándar)",
                    "--port 465 --security ssl        (SMTPS implícito)",
                    $"mail-tester --probe --host {options.Host}",
                ],
                Describe(result.Exception!));
        }

        if (result.Port == 465 && result.Security is SecurityMode.StartTls or SecurityMode.StartTlsIfAvailable)
        {
            return new FailureExplanation(
                Title(tls), tls, ExitCode.TlsFailure,
                "Pediste STARTTLS contra el puerto 465, que casi siempre es TLS implícito: el servidor arranca el handshake en el primer byte y nunca va a haber un EHLO en claro donde negociar STARTTLS.",
                [
                    "--port 465 --security ssl",
                    "--port 587 --security starttls",
                    $"mail-tester --probe --host {options.Host}",
                ],
                Describe(result.Exception!));
        }

        return new FailureExplanation(
            Title(tls), tls, ExitCode.TlsFailure,
            "El handshake falló sin errores de certificado, así que probablemente no hay versión de TLS ni cipher suite en común. Los servidores endurecidos rechazan TLS 1.0 y 1.1, y los servidores viejos no hablan TLS 1.2 ni 1.3.",
            [
                $"mail-tester --probe --host {options.Host}  (prueba las otras combinaciones de puerto y TLS)",
                "Confirmar con el administrador qué versiones de TLS acepta el servidor.",
                "Revisar si hay un proxy de inspección TLS en el medio interceptando la conexión.",
            ],
            Describe(result.Exception!));
    }

    static FailureExplanation FromNotSupported(AttemptResult result, CliOptions options, AttemptPhase phase)
    {
        if (phase == AttemptPhase.Authenticate)
        {
            return new FailureExplanation(
                Title(AttemptPhase.Authenticate), AttemptPhase.Authenticate, ExitCode.AuthenticationFailure,
                "El servidor no anunció ninguna capacidad AUTH utilizable, así que MailKit no tiene con qué autenticar.",
                [
                    "Muchos servidores solo ofrecen AUTH después de STARTTLS: probar --security starttls.",
                    "Si el relay no pide autenticación, usar --auth none y no pasar --user.",
                    $"Mecanismos que anunció el servidor: {Offered(result)}",
                ],
                Describe(result.Exception!));
        }

        return new FailureExplanation(
            Title(AttemptPhase.TlsHandshake), AttemptPhase.TlsHandshake, ExitCode.TlsFailure,
            $"Pediste --security {result.Security.ToCliName()}, que exige STARTTLS, y el servidor no anunció esa extensión en el EHLO.",
            [
                "--security starttls-if-available   (usa STARTTLS si está, sigue sin cifrar si no)",
                "--port 465 --security ssl          (por si el servidor solo hace TLS implícito)",
                $"mail-tester --probe --host {options.Host}",
            ],
            Describe(result.Exception!));
    }

    static FailureExplanation FromRejectedCredentials(AttemptResult result, CliOptions options, Exception exception) =>
        new(
            Title(AttemptPhase.Authenticate), AttemptPhase.Authenticate, ExitCode.AuthenticationFailure,
            "El servidor recibió las credenciales y las rechazó. La conexión y el TLS funcionan: el problema es la credencial o la política de la cuenta.",
            [
                "Confirmar si el usuario tiene que ser la dirección de mail completa o solo la parte local: depende del servidor.",
                "Si la cuenta tiene MFA, la password normal no sirve: hace falta un app password.",
                "Microsoft 365 trae SMTP AUTH deshabilitado a nivel organización y por buzón; hay que habilitarlo explícitamente.",
                "Gmail requiere app password u OAuth2; la password de la cuenta no funciona.",
                "Forzar un mecanismo a la vez para aislar: --auth plain, después --auth login.",
                "Si la password tiene caracteres especiales, pasarla como --pass=<valor> para que el shell no la altere.",
                $"Mecanismos que anunció el servidor: {Offered(result)}",
            ],
            Describe(exception));

    static FailureExplanation FromCommand(SmtpCommandException exception, AttemptResult result, CliOptions options, AttemptPhase phase)
    {
        var status = (int)exception.StatusCode;

        switch (exception.StatusCode)
        {
            case SmtpStatusCode.AuthenticationRequired when RequiresStartTls(exception.Message):
                return new FailureExplanation(
                    Title(AttemptPhase.Authenticate), AttemptPhase.Authenticate, ExitCode.AuthenticationFailure,
                    "El servidor contestó 530 5.7.0: exige STARTTLS antes de aceptar AUTH. No es un problema de credenciales, es que no acepta autenticación en claro.",
                    [
                        $"--port {result.Port} --security starttls",
                        "--port 465 --security ssl",
                    ],
                    Describe(exception));

            case SmtpStatusCode.AuthenticationRequired:
                return new FailureExplanation(
                    Title(phase), phase, ExitCode.AuthenticationFailure,
                    "El servidor contestó 530: pide autenticación y el intento fue anónimo.",
                    [
                        "Pasar --user y --pass.",
                        "Si esperabas un relay abierto, este servidor no lo es para tu IP de origen.",
                    ],
                    Describe(exception));

            case SmtpStatusCode.AuthenticationInvalidCredentials:
                return FromRejectedCredentials(result, options, exception);

            case SmtpStatusCode.AuthenticationMechanismTooWeak:
            case SmtpStatusCode.EncryptionRequiredForAuthenticationMechanism:
                return new FailureExplanation(
                    Title(AttemptPhase.Authenticate), AttemptPhase.Authenticate, ExitCode.AuthenticationFailure,
                    $"El servidor rechazó el mecanismo de autenticación por débil o por falta de cifrado ({status}).",
                    [
                        "Subir el canal: --security starttls o --security ssl.",
                        $"Probar otro mecanismo. Anunciados: {Offered(result)}",
                    ],
                    Describe(exception));

            case SmtpStatusCode.TemporaryAuthenticationFailure:
                return new FailureExplanation(
                    Title(AttemptPhase.Authenticate), AttemptPhase.Authenticate, ExitCode.AuthenticationFailure,
                    "El servidor reportó una falla temporal de autenticación (454). La credencial puede ser correcta y el backend estar caído o saturado.",
                    ["Reintentar en unos minutos.", "Consultar el estado del backend de autenticación."],
                    Describe(exception));

            case SmtpStatusCode.ExceededStorageAllocation:
                return new FailureExplanation(
                    Title(AttemptPhase.Send), AttemptPhase.Send, ExitCode.SmtpRejected,
                    "El servidor rechazó el mensaje por tamaño (552). Comparar con el valor SIZE que anunció en el EHLO, más arriba en las líneas CAPS.",
                    ["Usar un --body más corto.", "Consultar el límite real de tamaño del servidor."],
                    Describe(exception));

            case SmtpStatusCode.ServiceNotAvailable:
            case SmtpStatusCode.MailboxBusy:
            case SmtpStatusCode.ErrorInProcessing:
            case SmtpStatusCode.InsufficientStorage:
                return new FailureExplanation(
                    Title(phase), phase, ExitCode.SmtpRejected,
                    $"El servidor devolvió un error temporal ({status}): greylisting, throttling o falta de recursos. La configuración puede estar bien.",
                    [
                        "Reintentar en unos minutos: con greylisting el segundo intento suele pasar.",
                        "Si se repite siempre, consultar los límites de rate del servidor.",
                    ],
                    Describe(exception));

            default:
                return FromRejection(exception, result, options, status);
        }
    }

    static FailureExplanation FromRejection(SmtpCommandException exception, AttemptResult result, CliOptions options, int status)
    {
        var cause = exception.ErrorCode switch
        {
            SmtpErrorCode.SenderNotAccepted =>
                $"El servidor no acepta '{options.From?.Address ?? exception.Mailbox?.Address}' como remitente ({status}). Es la respuesta típica cuando la dirección no pertenece a la cuenta autenticada o al dominio que el servidor puede relayar.",

            SmtpErrorCode.RecipientNotAccepted =>
                $"El servidor rechazó al destinatario '{exception.Mailbox?.Address}' ({status}). O la casilla no existe, o el servidor no acepta relay hacia ese dominio desde esta conexión.",

            _ =>
                $"El servidor rechazó el mensaje con {status}. Suele ser política de relay, reputación de la IP de origen, o una regla antispam.",
        };

        var suggestions = exception.ErrorCode switch
        {
            SmtpErrorCode.SenderNotAccepted => new[]
            {
                "Usar en --from una dirección del dominio de la cuenta autenticada.",
                "Verificar si el servidor exige que --from coincida exactamente con --user.",
                "Si es un relay, confirmar que el dominio del remitente esté en su lista de dominios permitidos.",
            },

            SmtpErrorCode.RecipientNotAccepted => new[]
            {
                "Probar con un destinatario del mismo dominio del servidor para separar 'casilla inexistente' de 'relay denegado'.",
                "Si el destino es externo, confirmar que la cuenta tenga permitido enviar afuera.",
            },

            _ => new[]
            {
                "Leer la respuesta completa del servidor en las líneas S: de arriba: el texto suele nombrar la política exacta.",
                "Consultar con el administrador la política de relay para esta IP de origen.",
            },
        };

        return new FailureExplanation(
            Title(AttemptPhase.Send), AttemptPhase.Send, ExitCode.SmtpRejected, cause, suggestions, Describe(exception));
    }

    static FailureExplanation FromProtocol(AttemptResult result, CliOptions options, AttemptPhase phase) =>
        new(
            Title(phase), phase, ExitCode.NetworkFailure,
            $"Hay algo escuchando en el puerto {result.Port} pero no habla SMTP, o un intermediario está alterando la conversación. Las líneas S: de arriba muestran lo que contestó realmente.",
            [
                $"mail-tester --probe --host {options.Host}  (encuentra el puerto correcto)",
                "Confirmar que el puerto sea de SMTP y no de otro servicio.",
                "Si hay un proxy de inspección TLS, excluir este host de la inspección.",
            ],
            Describe(result.Exception!));

    static FailureExplanation Unclassified(Exception exception, AttemptPhase phase) =>
        new(
            Title(phase), phase, ExitCode.Unexpected,
            $"Falla no clasificada en la fase {phase}. El detalle técnico de abajo es todo lo que se sabe.",
            [
                "Volver a correr con --log-file y adjuntar el archivo al reporte.",
                "Las líneas C: y S: de arriba muestran hasta dónde llegó la conversación.",
            ],
            Describe(exception));

    static bool RequiresStartTls(string message) =>
        message.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)
        || message.Contains("5.7.0", StringComparison.Ordinal);

    /// <summary>Ports where every mainstream server treats STARTTLS as the only option, so a
    /// mismatch with implicit TLS can be named with confidence instead of merely suspected.</summary>
    static bool IsConventionalStartTlsPort(int port) => port is 587 or 25 or 2525;

    static string Offered(AttemptResult result) =>
        result.AuthMechanismsOffered.Count > 0
            ? string.Join(", ", result.AuthMechanismsOffered)
            : "ninguno";

    static string Title(AttemptPhase phase) => phase switch
    {
        AttemptPhase.Dns => "FALLA EN FASE: RESOLUCIÓN DNS",
        AttemptPhase.TcpConnect => "FALLA EN FASE: CONEXIÓN TCP",
        AttemptPhase.TlsHandshake => "FALLA EN FASE: HANDSHAKE TLS",
        AttemptPhase.Greeting => "FALLA EN FASE: SALUDO DEL SERVIDOR",
        AttemptPhase.Ehlo => "FALLA EN FASE: EHLO",
        AttemptPhase.Authenticate => "FALLA EN FASE: AUTENTICACIÓN",
        AttemptPhase.Send => "FALLA EN FASE: ENVÍO",
        AttemptPhase.Quit => "FALLA EN FASE: CIERRE DE SESIÓN",
        _ => "FALLA",
    };

    /// <summary>Type, message, and up to two levels of inner exception. Never a raw stack trace.</summary>
    static string Describe(Exception exception)
    {
        var detail = new StringBuilder($"{exception.GetType().Name}: {exception.Message}");
        var inner = exception.InnerException;

        for (var depth = 0; inner is not null && depth < 2; depth++, inner = inner.InnerException)
            detail.AppendLine().Append(new string(' ', (depth + 1) * 2)).Append("-> ").Append($"{inner.GetType().Name}: {inner.Message}");

        return detail.ToString();
    }
}
