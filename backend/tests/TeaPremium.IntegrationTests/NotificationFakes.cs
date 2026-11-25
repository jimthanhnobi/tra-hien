using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeaPremium.Application.Time;

namespace TeaPremium.IntegrationTests;

internal sealed record CapturedTelegramRequest(Uri RequestUri, string ChatId, string Text, string ParseMode, bool DisableWebPagePreview, string RawBody);

internal sealed class FakeTelegramHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> _responder;

    public ConcurrentQueue<CapturedTelegramRequest> Requests { get; } = new();

    public FakeTelegramHttpMessageHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>>? responder = null)
    {
        _responder = responder ?? ((_, _, _) => Task.FromResult(OkResponse()));
    }

    public static HttpResponseMessage OkResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":1}}", Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage StatusResponse(HttpStatusCode code, string json = "{\"ok\":false}") =>
        new(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var raw = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Requests.Enqueue(new CapturedTelegramRequest(
            request.RequestUri!,
            root.GetProperty("chat_id").GetString() ?? "",
            root.GetProperty("text").GetString() ?? "",
            root.GetProperty("parse_mode").GetString() ?? "",
            root.GetProperty("disable_web_page_preview").GetBoolean(),
            raw));

        return await _responder(request, raw, cancellationToken);
    }
}

internal sealed record CapturedSmtpMessage(string MailFrom, string RcptTo, string RawData, string? AuthPlain);

// In-memory X.509 certificate factory for STARTTLS tests (never writes .pfx/.pem to disk).
internal sealed class InMemoryStartTlsCertificateSet : IDisposable
{
    public X509Certificate2 RootCaPublicCert { get; }
    public X509Certificate2 ServerCert { get; }

    private InMemoryStartTlsCertificateSet(X509Certificate2 rootCaPublicCert, X509Certificate2 serverCert)
    {
        RootCaPublicCert = rootCaPublicCert;
        ServerCert = serverCert;
    }

    public static InMemoryStartTlsCertificateSet Create(string[] dnsNames, IPAddress[] ipAddresses)
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=TeaPremium Loopback Test Root CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, critical: false));
        using var rootCertWithKey = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(7));
        var rootPublic = X509CertificateLoader.LoadCertificate(rootCertWithKey.RawData);

        using var leafKey = RSA.Create(2048);
        var commonName = dnsNames.FirstOrDefault() ?? ipAddresses.FirstOrDefault()?.ToString() ?? "localhost";
        var leafRequest = new CertificateRequest($"CN={commonName}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames) san.AddDnsName(dns);
        foreach (var ip in ipAddresses) san.AddIpAddress(ip);
        leafRequest.CertificateExtensions.Add(san.Build());
        leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, critical: false));

        var serial = new byte[12];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;
        using var signedLeaf = leafRequest.Create(rootCertWithKey, now.AddHours(-1), now.AddDays(5), serial);
        var serverCert = signedLeaf.CopyWithPrivateKey(leafKey);

        return new InMemoryStartTlsCertificateSet(rootPublic, serverCert);
    }

    public void Dispose()
    {
        ServerCert.Dispose();
        RootCaPublicCert.Dispose();
    }
}

// Loopback-only fake SMTP server bound to 127.0.0.1:0 (zero real network traffic).
internal sealed class LoopbackSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2? _startTlsCertificate;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<int> _nextDataResponseCodes = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ConcurrentQueue<CapturedSmtpMessage> Messages { get; } = new();
    public ConcurrentQueue<string> ReceivedCommands { get; } = new();
    public int ConnectionCount => Volatile.Read(ref _connectionCount);
    public int AuthCommandCount => Volatile.Read(ref _authCommandCount);
    private int _connectionCount;
    private int _authCommandCount;

    public LoopbackSmtpServer(X509Certificate2? startTlsCertificate = null)
    {
        _startTlsCertificate = startTlsCertificate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void EnqueueDataResponseCode(int smtpCode) => _nextDataResponseCodes.Enqueue(smtpCode);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            Interlocked.Increment(ref _connectionCount);
            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            Stream transportStream = client.GetStream();
            SslStream? sslStream = null;
            StreamReader? reader = null;
            StreamWriter? writer = null;
            try
            {
                reader = new StreamReader(transportStream, Encoding.ASCII, leaveOpen: true);
                writer = new StreamWriter(transportStream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 loopback.teapremium.test ESMTP");
                var mailFrom = "";
                var rcptTo = "";
                string? authPlain = null;
                var tlsUpgraded = false;

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;

                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                    {
                        ReceivedCommands.Enqueue(tlsUpgraded ? "EHLO_TLS" : "EHLO");
                        await writer.WriteLineAsync("250-loopback.teapremium.test");
                        if (_startTlsCertificate is not null && !tlsUpgraded)
                            await writer.WriteLineAsync("250-STARTTLS");
                        await writer.WriteLineAsync("250 AUTH PLAIN");
                    }
                    else if (string.Equals(line, "STARTTLS", StringComparison.OrdinalIgnoreCase))
                    {
                        ReceivedCommands.Enqueue("STARTTLS");
                        if (_startTlsCertificate is null)
                        {
                            await writer.WriteLineAsync("454 TLS not available");
                            continue;
                        }

                        await writer.WriteLineAsync("220 Ready to start TLS");
                        await writer.FlushAsync(ct);
                        reader.Dispose();
                        writer.Dispose();

                        sslStream = new SslStream(transportStream, leaveInnerStreamOpen: false);
                        await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _startTlsCertificate,
                            ClientCertificateRequired = false
                        }, ct);

                        tlsUpgraded = true;
                        transportStream = sslStream;
                        reader = new StreamReader(transportStream, Encoding.ASCII, leaveOpen: true);
                        writer = new StreamWriter(transportStream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                    }
                    else if (line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _authCommandCount);
                        ReceivedCommands.Enqueue("AUTH");
                        if (line.StartsWith("AUTH PLAIN ", StringComparison.OrdinalIgnoreCase))
                            authPlain = line["AUTH PLAIN ".Length..].Trim();
                        await writer.WriteLineAsync("235 Authentication succeeded");
                    }
                    else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase))
                    {
                        ReceivedCommands.Enqueue("MAIL");
                        mailFrom = line["MAIL FROM:".Length..].Trim();
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                    {
                        ReceivedCommands.Enqueue("RCPT");
                        rcptTo = line["RCPT TO:".Length..].Trim();
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (string.Equals(line, "DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        ReceivedCommands.Enqueue("DATA");
                        if (_nextDataResponseCodes.TryDequeue(out var forcedCode) && forcedCode != 250)
                        {
                            await writer.WriteLineAsync($"{forcedCode} Simulated failure");
                            continue;
                        }

                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (true)
                        {
                            var dataLine = await reader.ReadLineAsync(ct);
                            if (dataLine is null || dataLine == ".") break;
                            data.AppendLine(dataLine);
                        }
                        Messages.Enqueue(new CapturedSmtpMessage(mailFrom, rcptTo, data.ToString(), authPlain));
                        await writer.WriteLineAsync("250 OK queued");
                    }
                    else if (string.Equals(line, "QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        ReceivedCommands.Enqueue("QUIT");
                        await writer.WriteLineAsync("221 Bye");
                        break;
                    }
                    else
                    {
                        await writer.WriteLineAsync("502 Command not implemented");
                    }
                }
            }
            catch
            {
                // Ignore TLS handshake rejection or disconnect during test assertions/teardown.
            }
            finally
            {
                reader?.Dispose();
                writer?.Dispose();
                if (sslStream is not null)
                    await sslStream.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}

internal sealed class NotificationLogCollector : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string EventName, string Message, Exception? Exception)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Sink(categoryName, Entries);
    public void Dispose() { }

    private sealed class Sink(
        string category,
        ConcurrentQueue<(string Category, LogLevel Level, string EventName, string Message, Exception? Exception)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue((category, logLevel, eventId.Name ?? "", formatter(state, exception), exception));
        }
    }
}

internal sealed class ManualNotificationClock(DateTimeOffset initialUtc) : TimeProvider, IClock
{
    private readonly Lock _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = initialUtc;

    public DateTimeOffset UtcNow => GetUtcNow();

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync) return _utcNow;
    }

    public void SetUtc(DateTimeOffset value)
    {
        ManualTimer[] due;
        lock (_sync)
        {
            _utcNow = value;
            due = ReadyTimers();
        }
        foreach (var timer in due) timer.Fire();
    }

    public void Advance(TimeSpan delta)
    {
        ManualTimer[] due;
        lock (_sync)
        {
            _utcNow += delta;
            due = ReadyTimers();
        }
        foreach (var timer in due) timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_sync)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period, _utcNow);
            _timers.Add(timer);
            return timer;
        }
    }

    private ManualTimer[] ReadyTimers() =>
        _timers.Where(t => t.TryConsume(_utcNow)).ToArray();

    private void Remove(ManualTimer timer)
    {
        lock (_sync) _timers.Remove(timer);
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualNotificationClock _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _nextDue;
        private TimeSpan _period;
        private bool _disposed;

        public ManualTimer(ManualNotificationClock owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period, DateTimeOffset now)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _period = period;
            _nextDue = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
        }

        public bool TryConsume(DateTimeOffset now)
        {
            if (_disposed || _nextDue is null || now < _nextDue.Value) return false;
            _nextDue = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : now + _period;
            return true;
        }

        public void Fire()
        {
            if (!_disposed) _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._sync)
            {
                if (_disposed) return false;
                _period = period;
                _nextDue = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._utcNow + dueTime;
                return true;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
