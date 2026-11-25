using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TeaPremium.Application.Notifications;
using TeaPremium.Application.Time;

[assembly: InternalsVisibleTo("TeaPremium.IntegrationTests")]

namespace TeaPremium.Infrastructure.Notifications;

public sealed record SmtpEnvelope(
    string Host,
    int Port,
    bool UseTls,
    string? Username,
    string? Password,
    string FromEmail,
    string FromName,
    string ToEmail,
    string Subject,
    string PlainTextBody,
    string? HtmlBody,
    TimeSpan ConnectTimeout,
    TimeSpan SendTimeout);

// ASSUMPTION-02: internal SMTP transport port backed by .NET BCL (0 new NuGet packages).
public interface ISmtpTransport
{
    Task SendAsync(SmtpEnvelope envelope, CancellationToken cancellationToken);
}

public sealed class BclSmtpTransport : ISmtpTransport
{
    private const string EhloHost = "teapremium.local";
    private readonly IClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly X509Certificate2? _testTrustedRootCertificate;
    private readonly string? _testConnectHostOverride;

    public BclSmtpTransport(IClock clock, TimeProvider timeProvider)
        : this(clock, timeProvider, testTrustedRootCertificate: null, testConnectHostOverride: null)
    {
    }

    // Internal test-only seam: configures CustomRootTrust (retaining full chain/expiry/SAN checks)
    // and optional loopback dial override without exposing any certificate bypass path in production.
    internal BclSmtpTransport(
        IClock clock,
        TimeProvider timeProvider,
        X509Certificate2? testTrustedRootCertificate = null,
        string? testConnectHostOverride = null)
    {
        _clock = clock;
        _timeProvider = timeProvider;
        _testTrustedRootCertificate = testTrustedRootCertificate;
        _testConnectHostOverride = testConnectHostOverride;
    }

    public async Task SendAsync(SmtpEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // Defense-in-depth (F1): never transmit AUTH credentials over cleartext to a non-loopback host.
        if (!envelope.UseTls
            && !string.IsNullOrWhiteSpace(envelope.Username)
            && !NotificationOptions.IsLoopbackHost(envelope.Host))
        {
            throw new NotificationDeliveryException("smtp_tls_required_for_auth");
        }

        using var totalTimeoutCts = new CancellationTokenSource(envelope.SendTimeout, _timeProvider);
        using var linkedTotal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, totalTimeoutCts.Token);
        var totalCt = linkedTotal.Token;

        var dialHost = _testConnectHostOverride ?? envelope.Host;
        using var tcp = new TcpClient();
        try
        {
            using var connectTimeoutCts = new CancellationTokenSource(envelope.ConnectTimeout, _timeProvider);
            using var linkedConnect = CancellationTokenSource.CreateLinkedTokenSource(totalCt, connectTimeoutCts.Token);
            await tcp.ConnectAsync(dialHost, envelope.Port, linkedConnect.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new NotificationDeliveryException("smtp_timeout");
        }
        catch (Exception)
        {
            throw new NotificationDeliveryException("smtp_connect_failed");
        }

        Stream transportStream = tcp.GetStream();
        SslStream? sslStream = null;
        try
        {
            var reader = new StreamReader(transportStream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var writer = new StreamWriter(transportStream, Encoding.ASCII, bufferSize: 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

            ExpectCode(await ReadResponseCodeAsync(reader, totalCt), 220, "smtp_protocol_error");
            await SendCommandAsync(writer, $"EHLO {EhloHost}", totalCt);
            ExpectCode(await ReadResponseCodeAsync(reader, totalCt), 250, "smtp_protocol_error");

            if (envelope.UseTls)
            {
                await SendCommandAsync(writer, "STARTTLS", totalCt);
                ExpectCode(await ReadResponseCodeAsync(reader, totalCt), 220, "smtp_tls_failed");

                reader.Dispose();
                writer.Dispose();

                sslStream = new SslStream(transportStream, leaveInnerStreamOpen: false);
                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = envelope.Host
                };
                if (_testTrustedRootCertificate is not null)
                {
                    var chainPolicy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                        RevocationMode = X509RevocationMode.NoCheck
                    };
                    chainPolicy.CustomTrustStore.Add(_testTrustedRootCertificate);
                    sslOptions.CertificateChainPolicy = chainPolicy;
                }

                try
                {
                    await sslStream.AuthenticateAsClientAsync(sslOptions, totalCt);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw new NotificationDeliveryException("smtp_timeout");
                }
                catch (AuthenticationException)
                {
                    throw new NotificationDeliveryException("smtp_tls_failed");
                }
                catch (Exception)
                {
                    throw new NotificationDeliveryException("smtp_tls_failed");
                }

                transportStream = sslStream;
                reader = new StreamReader(transportStream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                writer = new StreamWriter(transportStream, Encoding.ASCII, bufferSize: 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

                await SendCommandAsync(writer, $"EHLO {EhloHost}", totalCt);
                ExpectCode(await ReadResponseCodeAsync(reader, totalCt), 250, "smtp_protocol_error");
            }

            if (!string.IsNullOrWhiteSpace(envelope.Username))
            {
                var tlsActive = sslStream is { IsAuthenticated: true, IsEncrypted: true };
                if (!tlsActive && !NotificationOptions.IsLoopbackHost(envelope.Host))
                    throw new NotificationDeliveryException("smtp_tls_required_for_auth");

                var plainBytes = Encoding.UTF8.GetBytes($"\0{envelope.Username}\0{envelope.Password ?? ""}");
                var token = Convert.ToBase64String(plainBytes);
                await SendCommandAsync(writer, $"AUTH PLAIN {token}", totalCt);
                var authCode = await ReadResponseCodeAsync(reader, totalCt);
                if (authCode != 235)
                    throw new NotificationDeliveryException(MapSmtpCode(authCode, "smtp_auth_failed"));
            }

            await SendCommandAsync(writer, $"MAIL FROM:<{envelope.FromEmail}>", totalCt);
            ExpectCode(await ReadResponseCodeAsync(reader, totalCt), 250, "smtp_rejected");

            await SendCommandAsync(writer, $"RCPT TO:<{envelope.ToEmail}>", totalCt);
            var rcptCode = await ReadResponseCodeAsync(reader, totalCt);
            if (rcptCode is not (250 or 251))
                throw new NotificationDeliveryException(MapSmtpCode(rcptCode, "smtp_rejected"));

            await SendCommandAsync(writer, "DATA", totalCt);
            ExpectCode(await ReadResponseCodeAsync(reader, totalCt), 354, "smtp_protocol_error");

            var mime = BuildMimeMessage(envelope, _clock.UtcNow);
            await writer.WriteAsync(mime.AsMemory(), totalCt);
            await writer.WriteAsync("\r\n.\r\n".AsMemory(), totalCt);
            await writer.FlushAsync(totalCt);

            ExpectCode(await ReadResponseCodeAsync(reader, totalCt), 250, "smtp_rejected");

            try
            {
                await SendCommandAsync(writer, "QUIT", totalCt);
                _ = await ReadResponseCodeAsync(reader, totalCt);
            }
            catch
            {
                // Best-effort QUIT after server confirmed 250 OK for DATA.
            }

            reader.Dispose();
            writer.Dispose();
        }
        catch (NotificationDeliveryException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new NotificationDeliveryException("smtp_timeout");
        }
        catch (Exception)
        {
            throw new NotificationDeliveryException("smtp_io_error");
        }
        finally
        {
            if (sslStream is not null)
                await sslStream.DisposeAsync();
        }
    }

    private static async Task SendCommandAsync(StreamWriter writer, string command, CancellationToken ct)
    {
        await writer.WriteLineAsync(command.AsMemory(), ct);
        await writer.FlushAsync(ct);
    }

    private static async Task<int> ReadResponseCodeAsync(StreamReader reader, CancellationToken ct)
    {
        var code = -1;
        for (var lines = 0; lines < 64; lines++)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                throw new NotificationDeliveryException("smtp_connection_closed");
            if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw new NotificationDeliveryException("smtp_protocol_error");
            code = parsed;
            if (line.Length == 3 || line[3] == ' ')
                return code;
            if (line[3] != '-')
                throw new NotificationDeliveryException("smtp_protocol_error");
        }
        throw new NotificationDeliveryException("smtp_protocol_error");
    }

    private static void ExpectCode(int actual, int expected, string fallbackError)
    {
        if (actual != expected)
            throw new NotificationDeliveryException(MapSmtpCode(actual, fallbackError));
    }

    private static string MapSmtpCode(int code, string fallback) => code switch
    {
        >= 400 and < 500 => "smtp_transient_error",
        530 or 534 or 535 => "smtp_auth_failed",
        >= 500 and < 600 => fallback == "smtp_auth_failed" ? "smtp_auth_failed" : "smtp_rejected",
        _ => fallback
    };

    private static string BuildMimeMessage(SmtpEnvelope envelope, DateTimeOffset nowUtc)
    {
        var boundary = "tp-" + Guid.CreateVersion7().ToString("N");
        var messageId = $"<{Guid.CreateVersion7():N}@{EhloHost}>";
        var sb = new StringBuilder(1024);

        sb.Append("Date: ").Append(nowUtc.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Message-ID: ").Append(messageId).Append("\r\n");
        sb.Append("From: ").Append(EncodeRfc2047(envelope.FromName)).Append(" <").Append(envelope.FromEmail).Append(">\r\n");
        sb.Append("To: <").Append(envelope.ToEmail).Append(">\r\n");
        sb.Append("Subject: ").Append(EncodeRfc2047(envelope.Subject)).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\n");

        if (string.IsNullOrWhiteSpace(envelope.HtmlBody))
        {
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            AppendWrappedBase64(sb, envelope.PlainTextBody);
        }
        else
        {
            sb.Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            AppendWrappedBase64(sb, envelope.PlainTextBody);
            sb.Append("\r\n--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: text/html; charset=utf-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            AppendWrappedBase64(sb, envelope.HtmlBody);
            sb.Append("\r\n--").Append(boundary).Append("--");
        }

        // RFC 5321 §4.5.2 dot-stuffing safety (Base64 alphabet has no '.', kept as defense-in-depth).
        return sb.ToString().Replace("\r\n.", "\r\n..", StringComparison.Ordinal);
    }

    private static string EncodeRfc2047(string value)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return $"=?UTF-8?B?{base64}?=";
    }

    private static void AppendWrappedBase64(StringBuilder sb, string text)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        for (var i = 0; i < encoded.Length; i += 76)
        {
            if (i > 0) sb.Append("\r\n");
            var len = Math.Min(76, encoded.Length - i);
            sb.Append(encoded, i, len);
        }
    }
}

public sealed class SmtpNotificationChannel(NotificationOptions options, ISmtpTransport transport) : INotificationChannel
{
    public string Code => NotificationChannels.Email;

    public async Task SendAsync(NotificationMessage notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!options.IsSmtpConfigured)
            throw new NotificationDeliveryException("smtp_not_configured");

        var fromEmail = EmailSecurity.ValidateRecipient(options.SmtpFromEmail);
        var fromName = EmailSecurity.ValidateHeaderValue(options.SmtpFromName);
        var toEmail = EmailSecurity.ValidateRecipient(notification.Recipient);
        var subject = EmailSecurity.ValidateHeaderValue(notification.Subject);

        var envelope = new SmtpEnvelope(
            options.SmtpHost.Trim(),
            options.SmtpPort,
            options.SmtpUseTls,
            string.IsNullOrWhiteSpace(options.SmtpUsername) ? null : options.SmtpUsername.Trim(),
            string.IsNullOrWhiteSpace(options.SmtpPassword) ? null : options.SmtpPassword,
            fromEmail,
            fromName,
            toEmail,
            subject,
            notification.TextBody,
            notification.HtmlBody,
            options.SmtpConnectTimeout,
            options.SmtpSendTimeout);

        await transport.SendAsync(envelope, cancellationToken);
    }
}
