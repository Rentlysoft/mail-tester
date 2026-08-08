namespace MailTester.Cli;

internal static class HelpText
{
    public static string Render() =>
        """
        mail-tester — diagnostica configuraciones de servidores SMTP.

        USO
          mail-tester --host <h> --from <a> --to <b> [opciones]
          mail-tester --probe --host <h> [opciones]

        CONEXIÓN
          --host <host>           Hostname o IP del servidor SMTP. Obligatorio.
          --port <n>              Puerto TCP. Default: 587.
          --security <modo>       Estrategia de TLS. Default: auto. Ver MODOS DE SEGURIDAD.
          --timeout <seg>         Timeout de conexión y de cada comando. Default: 30 (10 con --probe).
          --ehlo-domain <dom>     Dominio que se anuncia en el EHLO. Default: el nombre de la máquina.
                                  Algunos servidores rechazan un EHLO que no resuelve.
          --allow-invalid-cert    Acepta el certificado del servidor aunque falle la validación.
                                  El certificado se reporta igual, con o sin este flag.

        MODOS DE SEGURIDAD
          none                    Sin cifrado, ni STARTTLS.                     (None)
          auto                    MailKit decide por puerto: 465 usa TLS
                                  implícito, cualquier otro intenta STARTTLS
                                  si el servidor lo ofrece.                     (Auto)
          starttls-if-available   Usa STARTTLS si se ofrece; si no, sigue
                                  sin cifrar.                                   (StartTlsWhenAvailable)
          starttls                Exige STARTTLS. Falla si no se ofrece.        (StartTls)
          ssl                     TLS implícito desde el primer byte, típico
                                  del puerto 465.                               (SslOnConnect)

        AUTENTICACIÓN
          --auth <mecanismo>      auto, plain, login, cram-md5, ntlm, none. Default: auto.
                                  'auto' negocia con lo que ofrece el servidor. Forzar un
                                  mecanismo sirve para aislar si el problema es el mecanismo
                                  o la credencial. 'none' saltea AUTH aunque se ofrezca.
          --user <usuario>        Sin --user no se autentica, que es como se prueba un relay abierto.
          --pass <password>       Obligatorio junto con --user, salvo con --auth none.

        MENSAJE
          --from <dirección>      Remitente. Obligatorio salvo con --probe.
          --to <dirección>        Destinatario. Obligatorio salvo con --probe. Repetible.
          --subject <texto>       Default: 'mail-tester <timestamp UTC>'.
          --body <texto>          Default: un cuerpo de prueba con los datos de la conexión.

        DIAGNÓSTICO Y SALIDA
          --probe                 Barre combinaciones de puerto y TLS, autentica, y reporta una
                                  matriz de qué funciona. No envía ningún mensaje.
          --log-file <ruta>       Duplica todo el log a un archivo, para adjuntarlo a un ticket.
          --show-secrets          No redacta las credenciales en el log del protocolo.
          --no-color              Sin color. También se desactiva solo si la salida está
                                  redirigida o si existe la variable NO_COLOR.
          --help, -h              Esta ayuda.

        EXIT CODES
          0  envío exitoso
          1  error inesperado
          2  argumentos inválidos
          3  falla de red (DNS, conexión rechazada, host inalcanzable)
          4  falla de TLS (handshake, certificado, STARTTLS no disponible)
          5  falla de autenticación
          6  el servidor rechazó el envío (relay denegado, destinatario, tamaño, throttling)
          7  timeout

        EJEMPLOS
          Descubrir qué configuración funciona:
            mail-tester --probe --host smtp.foo.com --user a@x.com --pass secreto

          Enviar por submission con STARTTLS:
            mail-tester --host smtp.foo.com --port 587 --security starttls --auth auto \
                        --user a@x.com --pass secreto --from a@x.com --to b@y.com

          Enviar por SMTPS implícito:
            mail-tester --host smtp.foo.com --port 465 --security ssl \
                        --user a@x.com --pass secreto --from a@x.com --to b@y.com

          Relay interno sin autenticación, sin cifrado:
            mail-tester --host 10.0.0.25 --port 25 --security none --auth none \
                        --from app@interno --to soporte@interno

        NOTAS
          Un valor que arranca con '--' se consume igual como valor: 'mail-tester --pass --raro'
          toma '--raro' como password. Para valores con espacios o caracteres raros, usá
          --pass=<valor> o comillas.

        """;
}
