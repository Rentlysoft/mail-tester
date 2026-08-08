# mail-tester

Herramienta de consola para diagnosticar la configuración SMTP de un servidor de correo:
puerto correcto, modo de TLS, mecanismo de autenticación, y si el servidor efectivamente
acepta un mensaje. Pensada para quien administra o da soporte a un servidor de mail y
necesita saber, con certeza y sin adivinar, por qué un cliente no puede enviar.

`mail-tester` **no** es:

- Un cliente de correo: no lee bandejas de entrada, no guarda mensajes, no tiene libreta de
  contactos.
- Un servidor de pruebas: no recibe correo, no simula un SMTP, no reemplaza a Mailtrap ni a
  un buzón descartable.

Habla el protocolo SMTP en vivo contra el servidor real que le indiques, narra cada paso a
medida que ocurre, y cuando algo falla explica en español cuál es la causa más probable y
qué comando correr para confirmarla.

## Instalación y ejecución

Requiere el SDK de .NET 8 para compilar o correr desde el código fuente.

Correr desde el fuente, sin publicar nada:

```
dotnet run --project src/MailTester -- --help
```

Todo lo que va después de `--` se le pasa tal cual a la herramienta.

Publicar un binario único y autocontenido (no necesita el runtime de .NET instalado en la
máquina de destino):

```
./publish.ps1
```

Genera `dist/win-x64/mail-tester.exe` y `dist/linux-x64/mail-tester`, de aproximadamente
71 MB cada uno. Una vez publicado, se invoca directamente:

```
./dist/win-x64/mail-tester.exe --help
```

Los ejemplos de este documento usan `mail-tester` a secas, como se invoca al binario
publicado (o a `dist/<runtime>/mail-tester[.exe]` si no está en el PATH). Corriendo desde el
código fuente, reemplazá `mail-tester` por `dotnet run --project src/MailTester --`.

## Arranque rápido

Cuatro comandos cubren la mayoría de los casos.

Descubrir qué configuración funciona, sin enviar nada (`--probe` no necesita `--from` ni
`--to`, porque nunca construye un mensaje):

```
mail-tester --probe --host smtp.foo.com --user a@x.com --pass secreto
```

Enviar por submission con STARTTLS (el caso más común hoy):

```
mail-tester --host smtp.foo.com --port 587 --security starttls --user a@x.com --pass secreto --from a@x.com --to b@y.com
```

Enviar por SMTPS con TLS implícito:

```
mail-tester --host smtp.foo.com --port 465 --security ssl --user a@x.com --pass secreto --from a@x.com --to b@y.com
```

Relay interno sin autenticación ni cifrado:

```
mail-tester --host 10.0.0.25 --port 25 --security none --auth none --from app@interno --to soporte@interno
```

### Qué esperar

El bloque siguiente es salida real, recortada, de una corrida contra un servidor de prueba
local (por eso el host es `127.0.0.1` y el puerto es uno efímero en lugar de 587; el formato
es idéntico al que vas a ver contra un servidor real con STARTTLS). Muestra el encabezado, el
handshake, la autenticación, el envío y el cierre:

```
[00:00.001] INFO  mail-tester · .NET 8.0.29 · MailKit 4.17.0.0
[00:00.002] CONF  host=127.0.0.1:51802 security=starttls auth=auto user=bob@fake.local pass=***
[00:00.002] CONF  from=a@x.com to=b@y.com
[00:00.002] CONF  timeout=5s ehlo-domain=PABLO-LEGION allow-invalid-cert=True
[00:00.020] STEP  1/6 Resolviendo DNS de 127.0.0.1
[00:00.021] INFO  El host es una IP literal: no hay resolución DNS que hacer.
[00:00.023] OK    127.0.0.1  (1 ms)
[00:00.024] STEP  2/6 Conectando TCP a 127.0.0.1:51802 (presupuesto total del intento: 5s)
[00:00.025] OK    conectado a 127.0.0.1:51802 desde 127.0.0.1:51803  (1 ms)
[00:00.025] STEP  3/6 Handshake SMTP: saludo, EHLO y TLS (starttls)
S: 220 fake.local ESMTP FakeServer
C: EHLO PABLO-LEGION
S: 250-fake.local
S: 250-SIZE 35882577
S: 250-AUTH PLAIN LOGIN
S: 250-STARTTLS
S: 250 8BITMIME
C: STARTTLS
S: 220 2.0.0 Ready to start TLS
[00:00.099] CERT  CN=fake.local · issuer=CN=fake.local
[00:00.102] CERT  válido 2026-08-07 .. 2026-09-07 (29 días restantes) · SAN: fake.local, 127.0.0.1
[00:00.102] CERT  thumbprint=4AEB6D6D47C6AA5DDB26A2E067C6EC91971562C6 · firma=sha256ECDSA
[00:00.103] WARN  La validación del certificado falló: RemoteCertificateChainErrors
[00:00.103] WARN    RemoteCertificateChainErrors: la cadena no valida: puede estar expirado, autofirmado, o faltar el intermedio
[00:00.104] WARN    cadena UntrustedRoot: A certificate chain processed, but terminated in a root certificate which is not trusted by the trust provider.
[00:00.104] WARN  Certificado inválido aceptado por --allow-invalid-cert. La conexión sigue, pero no está verificada.
C: EHLO PABLO-LEGION
S: 250-fake.local
S: 250-SIZE 35882577
S: 250-AUTH PLAIN LOGIN
S: 250-STARTTLS
S: 250 8BITMIME
[00:00.107] CAPS  SIZE · AUTHENTICATION · EIGHTBITMIME · STARTTLS · SIZE=35882577 · AUTH=LOGIN PLAIN
[00:00.107] OK    handshake completo · Tls13 · TLS_AES_256_GCM_SHA384  (82 ms)
[00:00.107] STEP  4/6 Autenticando como bob@fake.local (mecanismo: negociado por MailKit)
C: AUTH PLAIN ***REDACTED***
S: 235 2.7.0 Authentication successful
[00:00.113] OK    autenticado como bob@fake.local  (6 ms)
[00:00.127] STEP  5/6 Enviando mensaje a 1 destinatario(s) · Message-Id ZFASAU3VZTU4.3T6TADY2U0LH1@pablo-legion
C: MAIL FROM:<a@x.com> SIZE=559 BODY=8BITMIME
S: 250 2.1.0 Ok
C: RCPT TO:<b@y.com>
S: 250 2.1.5 Ok
C: DATA
S: 354 End data with <CR><LF>.<CR><LF>
[... cuerpo del mensaje omitido ...]
S: 250 2.0.0 Ok: queued as 2A9F1B0C3D
[00:00.143] SENT  aceptado: 2.0.0 Ok: queued as 2A9F1B0C3D  (30 ms)
[00:00.143] STEP  6/6 Cerrando la sesión (QUIT)
C: QUIT
S: 221 2.0.0 Bye
[00:00.145] OK    sesión cerrada  (2 ms)

[00:00.147] OK    RESULTADO: ÉXITO · total 140 ms · exit code 0
[00:00.147] INFO  Respuesta del servidor: 2.0.0 Ok: queued as 2A9F1B0C3D
[00:00.147] INFO  Buscá este Message-Id en los logs del servidor si el mensaje no aparece: ZFASAU3VZTU4.3T6TADY2U0LH1@pablo-legion
```

Con `--allow-invalid-cert` la corrida sigue aunque el certificado no valide (arriba, porque
el servidor de prueba usa uno autofirmado); sin ese flag, la misma cadena rota habría hecho
fallar el handshake con exit code 4.

## Referencia completa de parámetros

### Conexión

| Flag | Default | Descripción |
|---|---|---|
| `--host <host>` | — (obligatorio) | Hostname o IP del servidor SMTP. |
| `--port <n>` | `587` | Puerto TCP. |
| `--security <modo>` | `auto` | Estrategia de TLS. Ver [Modos de seguridad](#modos-de-seguridad). |
| `--timeout <seg>` | `30` (`10` con `--probe`) | Timeout de conexión y de cada comando, en segundos. |
| `--ehlo-domain <dom>` | nombre de la máquina | Dominio que se anuncia en el EHLO. Algunos servidores rechazan un EHLO que no resuelve. |
| `--allow-invalid-cert` | desactivado | Acepta el certificado del servidor aunque falle la validación. El certificado se reporta igual, con o sin este flag. |

### Autenticación

| Flag | Default | Descripción |
|---|---|---|
| `--auth <mecanismo>` | `auto` | `auto`, `plain`, `login`, `cram-md5`, `ntlm`, `none`. Ver [Mecanismos de autenticación](#mecanismos-de-autenticación). |
| `--user <usuario>` | — | Usuario para autenticar. Sin `--user` no se autentica, que es como se prueba un relay abierto. |
| `--pass <password>` | — | Obligatorio junto con `--user`, salvo con `--auth none`. |

### Mensaje

| Flag | Default | Descripción |
|---|---|---|
| `--from <dirección>` | — (obligatorio salvo con `--probe`) | Remitente del mensaje. |
| `--to <dirección>` | — (obligatorio salvo con `--probe`) | Destinatario. Repetible: se puede pasar varias veces. |
| `--subject <texto>` | `mail-tester <timestamp UTC>` | Asunto del mensaje. Rechazado con exit code 2 si se combina con `--probe`, que no envía ningún mensaje. |
| `--body <texto>` | cuerpo de prueba con los datos de la conexión | Cuerpo del mensaje. Rechazado con exit code 2 si se combina con `--probe`, que no envía ningún mensaje. |

### Diagnóstico y salida

| Flag | Default | Descripción |
|---|---|---|
| `--probe` | desactivado | Barre combinaciones de puerto y TLS, autentica, y reporta una matriz de qué funciona. No envía ningún mensaje. |
| `--log-file <ruta>` | — | Duplica todo el log a un archivo, para adjuntarlo a un ticket. |
| `--show-secrets` | desactivado | No redacta las credenciales en el log del protocolo. |
| `--no-color` | desactivado | Sin color. También se desactiva solo si la salida está redirigida o si existe la variable `NO_COLOR`. |
| `--help`, `-h` | — | Muestra la ayuda y termina con exit code 0. |

Un valor que arranca con `--` se consume igual como valor: `mail-tester --pass --raro` toma
`--raro` como password. Para valores con espacios o caracteres raros, usá `--pass=<valor>` o
comillas.

## Modos de seguridad

| Valor | `SecureSocketOptions` de MailKit | Qué hace |
|---|---|---|
| `none` | `None` | Sin cifrado, ni STARTTLS. |
| `auto` | `Auto` | MailKit decide por puerto: el 465 usa TLS implícito, cualquier otro intenta STARTTLS si el servidor lo ofrece. |
| `starttls-if-available` | `StartTlsWhenAvailable` | Usa STARTTLS si se ofrece; si no, sigue sin cifrar. |
| `starttls` | `StartTls` | Exige STARTTLS. Falla si el servidor no lo ofrece. |
| `ssl` | `SslOnConnect` | TLS implícito desde el primer byte, típico del puerto 465. |

`auto` es el default, y decide en función del **puerto**, no del contenido del EHLO: si le
pasás `--port 465` sin `--security`, asume TLS implícito; para cualquier otro puerto, negocia
STARTTLS si el servidor lo anuncia y sigue sin cifrar si no lo anuncia. Cuando ya sabés qué
espera el servidor, conviene fijar el modo explícitamente (`starttls` o `ssl`) en lugar de
dejarlo en `auto`, porque un modo explícito falla con un diagnóstico claro en vez de degradar
silenciosamente a texto plano.

## Mecanismos de autenticación

| Valor | Mecanismo SASL forzado | Cuándo usarlo |
|---|---|---|
| `auto` | — (negocia) | Default. Deja que MailKit elija entre lo que el servidor anuncia. |
| `plain` | `PLAIN` | Forzar para aislar si el problema es el mecanismo o la credencial. |
| `login` | `LOGIN` | Idem; algunos servidores viejos solo ofrecen `LOGIN`. |
| `cram-md5` | `CRAM-MD5` | Idem; challenge-response, cada vez menos común. |
| `ntlm` | `NTLM` | Idem; típico de Exchange on-premises. |
| `none` | — | Saltea AUTH aunque el servidor lo ofrezca. Sirve para probar un relay sin credenciales. |

**Sin `--user` no se autentica**, sin importar qué mecanismo se haya pasado en `--auth`: es la
forma documentada de probar un relay abierto. Un mecanismo forzado que el servidor no anunció
en el EHLO se intenta igual — lo que responda el servidor es información, no un motivo para no
probarlo — y la corrida avisa con un `WARN` que el mecanismo no estaba anunciado.

## Cómo leer la salida

Cada corrida imprime primero un encabezado (versión de la herramienta, host, puerto,
seguridad, autenticación, y si corresponde `from`/`to`), y después narra **6 pasos**
numerados `n/6`: **DNS**, **TCP**, **handshake** (saludo, EHLO y STARTTLS juntos), **AUTH**,
**SEND** y **QUIT**. El handshake es un solo paso porque MailKit hace el saludo, el EHLO y el
STARTTLS en una sola llamada: presentarlos como pasos separados afirmaría un control sobre esa
secuencia que la herramienta no tiene.

Un paso que no aplica se imprime igual, con su motivo, en lugar de omitirse en silencio. Por
ejemplo, sin `--user`:

```
STEP  4/6 AUTH — omitido (sin credenciales)
```

o con `--probe`, que nunca envía nada:

```
STEP  5/6 SEND — omitido (--probe no envía mensajes)
```

Cada línea lleva un nivel a la izquierda:

| Nivel | Significado |
|---|---|
| `INFO` | Información de contexto: versión, resultado de DNS, avisos generales. |
| `CONF` | Configuración efectiva de la corrida (host, seguridad, credenciales enmascaradas, timeout). Con `--probe`, el puerto y la seguridad solo se muestran con un valor fijo cuando se pasaron explícitamente por línea de comandos; si el barrido los recorre, la línea dice `barre` en su lugar. |
| `STEP` | Arranque de uno de los 6 pasos, con su número `n/6`. |
| `OK` | Un paso terminó bien, con cuánto tardó. |
| `CAPS` | Las capacidades que anunció el servidor en el EHLO (`SIZE`, `AUTH`, extensiones, etc.). |
| `CERT` | Datos del certificado presentado por el servidor: sujeto, emisor, vigencia, SAN, huella digital. Se imprime siempre que hay TLS, válido o no. |
| `WARN` | Algo fuera de lo ideal que no interrumpe la corrida: certificado inválido aceptado por `--allow-invalid-cert`, mecanismo forzado no anunciado, etc. |
| `FAIL` | Un paso falló. Va seguido del bloque de diagnóstico (causa probable, qué probar, detalle técnico) y de la línea `RESULTADO`. |
| `SENT` | El servidor aceptó el mensaje para envío. |

Las líneas `C:` y `S:` son el diálogo SMTP tal cual viaja por la red: `C:` es lo que mandó
esta herramienta, `S:` lo que contestó el servidor. Por defecto, la credencial que viaja en el
`AUTH` se reemplaza por `***REDACTED***`; ver [Seguridad y credenciales](#seguridad-y-credenciales).

Al final, una línea `RESULTADO` resume la corrida. Si fue exitosa, dice `ÉXITO`, el tiempo
total y el exit code (`RESULTADO: ÉXITO · total 140 ms · exit code 0`, como en el ejemplo de
arriba). Si no, dice `FALLA` o `INTERRUMPIDO` (una corrida cortada por Ctrl+C, que no es una
falla de configuración), y ahí sí suma la fase en la que terminó junto con el exit code.

## Tabla de exit codes

| Code | Significado |
|---|---|
| `0` | Envío exitoso; con `--probe`, que no envía nada, significa que al menos una combinación funcionó. |
| `1` | Error inesperado en la herramienta, o corrida interrumpida (Ctrl+C) antes de terminar. |
| `2` | Argumentos inválidos. |
| `3` | Falla de red: DNS que no resuelve, conexión rechazada, host inalcanzable, o un puerto que responde pero no habla SMTP. |
| `4` | Falla de TLS: handshake, certificado, o STARTTLS no disponible cuando se exigió. |
| `5` | Falla de autenticación. |
| `6` | El servidor rechazó el envío: relay denegado, destinatario, tamaño, throttling. |
| `7` | Timeout: ninguna de las partes cerró la conexión, simplemente no contestó a tiempo. |

Un puerto que acepta la conexión TCP pero no habla SMTP también cae en el código `3`: el
saludo se intercambia siempre en texto plano incluso bajo STARTTLS, así que esa falla nunca
llegó a tocar TLS, y devolver un código de TLS mandaría a un script por la rama equivocada.

Ejemplo de uso en un script:

```powershell
mail-tester --host smtp.foo.com --port 587 --security starttls --user a@x.com --pass secreto --from a@x.com --to b@y.com
switch ($LASTEXITCODE) {
    0       { Write-Host "Envío OK" }
    5       { Write-Host "Revisar usuario y password" }
    { $_ -in 3,4,7 } { Write-Host "Problema de red o TLS, no de credenciales" }
    default { Write-Host "Falló con exit code $LASTEXITCODE" }
}
```

## Seguridad y credenciales

- Por defecto, la credencial se redacta del log del protocolo: el comando `AUTH` y la
  respuesta a un desafío `334` se reemplazan por `***REDACTED***` en las líneas `C:`.
- `--show-secrets` desactiva esa redacción y muestra la credencial real en el diálogo `C:`/`S:`.
- El encabezado de la corrida (línea `CONF`) nunca muestra la contraseña, y tampoco su
  longitud (`pass=***`, sin más): un log termina pegado en un ticket, y ni siquiera el largo
  de la credencial es un dato que convenga publicar ahí. Esto no cambia con `--show-secrets`,
  que solo afecta el diálogo de protocolo (líneas `C:`/`S:`), no el encabezado.
- Usá `--pass=<valor>` (con el `=` pegado) en lugar de `--pass <valor>` cuando la contraseña
  tenga caracteres especiales, para que la shell no la altere antes de que llegue a la
  herramienta.
- **Un `--log-file` combinado con `--show-secrets` deja la credencial en texto plano en
  disco**, porque el archivo recibe exactamente las mismas líneas que la consola.

## Diagnóstico de problemas frecuentes

| Síntoma | Causa probable | Qué correr |
|---|---|---|
| Handshake TLS falla contra el puerto 587 | Pediste `--security ssl` (TLS implícito) contra un puerto que espera STARTTLS. | `mail-tester --host <host> --port 587 --security starttls ...` |
| Handshake TLS falla contra el puerto 465 | Pediste `--security starttls` contra un puerto que casi siempre es TLS implícito. | `mail-tester --host <host> --port 465 --security ssl ...` |
| El servidor contesta `530 5.7.0` al intentar autenticar | Exige STARTTLS antes de aceptar `AUTH`; no es un problema de credenciales. | `mail-tester --host <host> --port 587 --security starttls --user ... --pass ...` |
| El servidor contesta `530 5.7.1` (u otro 530 sin mención de STARTTLS) | Pide autenticación y el intento fue anónimo. | Agregar `--user` y `--pass`; si esperabas un relay abierto, este servidor no lo es para tu IP de origen. |
| `535` al autenticar, y la cuenta tiene MFA | La password normal no sirve con MFA activo: hace falta un app password. | Generar un app password en la cuenta y usarlo en `--pass`. |
| `535` contra un tenant de Microsoft 365 | SMTP AUTH está deshabilitado a nivel organización o a nivel buzón; Microsoft lo trae apagado por default. | Habilitar SMTP AUTH en el tenant o el buzón, después reintentar el mismo comando. |
| La conexión TCP se cuelga hasta el timeout, sin aceptar ni rechazar | Un firewall está descartando los paquetes en silencio; un servicio caído rechaza la conexión, no la ignora. | `mail-tester --probe --host <host>` desde otra red, para descartar el firewall local. |
| Puerto 25 rechazado o nunca contesta | Muchos proveedores de cloud bloquean el 25 de salida por default. | Probar `--port 587 --security starttls` en su lugar. |
| El certificado no valida (`RemoteCertificateChainErrors`) | Certificado autofirmado, cadena incompleta, o expirado. | Revisar las líneas `CERT` de la corrida; `--allow-invalid-cert` permite seguir el diagnóstico sin verificar la identidad. |

Cada uno de estos casos, cuando ocurre, imprime además un bloque completo de "Causa más
probable" / "Qué probar" / "Detalle técnico" — la tabla de arriba es un índice, no un
reemplazo de esa explicación.

## Qué no hace

- No soporta XOAUTH2 ni ningún flujo de OAuth2: solo los cuatro mecanismos SASL de la tabla de
  autenticación (`plain`, `login`, `cram-md5`, `ntlm`; `auto` y `none` no son mecanismos SASL,
  son formas de negociar o de saltear la autenticación).
- No soporta autenticación por certificado de cliente (mTLS).
- No verifica la recepción del mensaje por IMAP ni POP3: confirma que el servidor lo aceptó
  para envío, no que llegó a destino.
- No adjunta archivos ni envía HTML: el cuerpo es siempre texto plano.
- No hace ningún chequeo de SPF, DKIM ni DMARC.

## Desarrollo

```
dotnet build
dotnet test
```

El build trata los warnings como errores, así que cualquier warning nuevo rompe el build. Al
cierre de este documento, la suite tiene 304 tests, todos en verde, ninguno saltado.

Mapa de directorios de `src/MailTester`:

| Directorio | Contenido |
|---|---|
| `Cli` | Parseo de argumentos, las opciones ya validadas, los enums de seguridad y autenticación, y el texto de `--help`. |
| `Output` | Todo lo que llega a pantalla o al `--log-file`: el log con niveles y color, el redactor de credenciales, y el logger que imprime el diálogo SMTP. |
| `Smtp` | El intento SMTP en sí: conexión, detección de fase, inspección de certificado, y la matriz de combinaciones de `--probe`. |
| `Modes` | Los dos modos de ejecución (envío y `--probe`) y el encabezado que se imprime al arrancar cualquiera de los dos. |
| `Errors` | Los exit codes y el traductor que convierte una excepción de MailKit en una explicación para un humano. |
| `Messages` | Construcción del mensaje de prueba: asunto y cuerpo por defecto cuando no se pasan `--subject` o `--body`. |
