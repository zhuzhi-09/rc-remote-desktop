using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Rc.Protocol;

public sealed class WsClientOptions
{
    /// <summary>Target endpoint, e.g. <c>wss://relay.example.com/agent?id=endpoint-1</c>.</summary>
    public required string Url { get; init; }

    /// <summary>Shared secret sent in the <c>X-RC-Token</c> header.</summary>
    public string? Token { get; init; }

    /// <summary>
    /// Optional IPv4/IPv6 address to connect to instead of resolving the hostname in <see cref="Url"/>.
    /// The URL's hostname is still used for TLS SNI and the <c>Host</c> header, so the traffic looks
    /// completely normal while DNS poisoning on the local network is avoided entirely.
    /// </summary>
    public string? ConnectAddress { get; init; }

    /// <summary>Accept a self-signed or otherwise untrusted server certificate.</summary>
    public bool AllowUntrustedCert { get; init; }

    /// <summary>
    /// Hex-encoded SHA-256 of the server leaf certificate. When set, the certificate must match
    /// exactly and the system trust chain is not consulted. Strongly recommended over
    /// <see cref="AllowUntrustedCert"/> because it also defeats MITM on the local network.
    /// </summary>
    public string? PinnedCertSha256 { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Minimal dependency-free WebSocket client built directly on a TCP socket (and an optional
/// <see cref="SslStream"/>). Compared with <see cref="ClientWebSocket"/> this gives us:
/// <list type="bullet">
/// <item><c>TCP_NODELAY</c>, removing Nagle-induced input lag.</item>
/// <item>Certificate pinning for MITM resistance.</item>
/// <item>A plain, unremarkable TLS handshake (valid SNI + Host) that blends into normal HTTPS.</item>
/// </list>
/// </summary>
public static class WsClient
{
    public static async Task<WebSocket> ConnectAsync(WsClientOptions options, CancellationToken ct = default)
    {
        var uri = new Uri(options.Url, UriKind.Absolute);
        var secure = uri.Scheme switch
        {
            "wss" => true,
            "ws" => false,
            _ => throw new ArgumentException($"Unsupported scheme '{uri.Scheme}'. Use ws:// or wss://."),
        };

        var port = uri.IsDefaultPort ? (secure ? 443 : 80) : uri.Port;
        var host = uri.Host;
        var pathAndQuery = uri.PathAndQuery;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.ConnectTimeout);

        // Connect to the override address when supplied, otherwise resolve the URL hostname.
        // IP literals (v4 or v6) skip DNS completely, which is exactly what we want on networks
        // that return poisoned answers for the resolver the network forces you to use.
        var socket = await ConnectSocketAsync(options.ConnectAddress ?? host, port, timeoutCts.Token)
            .ConfigureAwait(false);

        Stream stream = new NetworkStream(socket, ownsSocket: true);

        if (secure)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, chain, errors) =>
                ValidateCertificate(cert, chain, errors, options, host));
            try
            {
                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                };
                await ssl.AuthenticateAsClientAsync(sslOptions, timeoutCts.Token).ConfigureAwait(false);
            }
            catch
            {
                ssl.Dispose();
                throw;
            }
            stream = ssl;
        }

        try
        {
            var keyBytes = RandomNumberGenerator.GetBytes(16);
            var key = Convert.ToBase64String(keyBytes);
            var expectedAccept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

            // IPv6 literals must be bracketed in the Host header (RFC 3986).
            var hostForHeader = host.Contains(':') ? $"[{host}]" : host;
            var hostHeader = port is 443 or 80 ? hostForHeader : $"{hostForHeader}:{port}";
            var request = new StringBuilder()
                .Append("GET ").Append(string.IsNullOrEmpty(pathAndQuery) ? "/" : pathAndQuery).Append(" HTTP/1.1\r\n")
                .Append("Host: ").Append(hostHeader).Append("\r\n")
                .Append("Upgrade: websocket\r\n")
                .Append("Connection: Upgrade\r\n")
                .Append("Sec-WebSocket-Key: ").Append(key).Append("\r\n")
                .Append("Sec-WebSocket-Version: 13\r\n")
                .Append("User-Agent: Mozilla/5.0\r\n");
            if (!string.IsNullOrEmpty(options.Token))
            {
                request.Append("X-RC-Token: ").Append(options.Token).Append("\r\n");
            }
            request.Append("\r\n");

            var requestBytes = Encoding.ASCII.GetBytes(request.ToString());
            await stream.WriteAsync(requestBytes, timeoutCts.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

            var headerText = await ReadHttpHeadersAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            if (!headerText.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
            {
                var body = await ReadErrorBodyAsync(stream, timeoutCts.Token).ConfigureAwait(false);
                var firstLine = headerText.Split("\r\n", 2)[0];
                var detail = string.IsNullOrWhiteSpace(body)
                    ? headerText.Replace("\r\n", " | ")
                    : body.Trim();
                throw new WebSocketException($"WebSocket upgrade rejected by server ({firstLine}): {detail}");
            }

            if (!headerText.Contains(expectedAccept, StringComparison.OrdinalIgnoreCase))
            {
                throw new WebSocketException("Server returned an invalid Sec-WebSocket-Accept value.");
            }

            return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = false,
                KeepAliveInterval = TimeSpan.FromSeconds(20),
            });
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a TCP connection, trying every resolved address in turn so a host that publishes both
    /// A and AAAA records still works when only one address family is routable from this network.
    /// IP literals never touch DNS.
    /// </summary>
    private static async Task<Socket> ConnectSocketAsync(string host, int port, CancellationToken ct)
    {
        var addresses = await ResolveAsync(host, ct).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                return socket;
            }
            catch (Exception ex)
            {
                lastError = ex;
                socket.Dispose();
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var literal = host.Trim('[', ']').Trim();
        if (IPAddress.TryParse(literal, out var parsed))
        {
            return [parsed];
        }

        var resolved = await Dns.GetHostAddressesAsync(literal, ct).ConfigureAwait(false);
        if (resolved.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }
        return resolved;
    }

    private static bool ValidateCertificate(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        WsClientOptions options,
        string host)
    {
        if (certificate is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.PinnedCertSha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
            return string.Equals(actual, Normalize(options.PinnedCertSha256), StringComparison.OrdinalIgnoreCase);
        }

        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        return options.AllowUntrustedCert;
    }

    private static string Normalize(string hex) =>
        hex.Replace(":", "").Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();

    /// <summary>Best-effort read of the response body so upgrade failures are diagnosable.</summary>
    private static async Task<string> ReadErrorBodyAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[2048];
        using var ms = new MemoryStream();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            int read;
            while (ms.Length < buffer.Length && (read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                ms.Write(buffer, 0, read);
            }
        }
        catch (Exception)
        {
            // The server may close without a body; whatever we already read is enough.
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<string> ReadHttpHeadersAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var sb = new StringBuilder();
        while (sb.Length < 32 * 1024)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Connection closed while reading the WebSocket handshake response.");
            }
            sb.Append((char)buffer[0]);
            if (sb.Length >= 4 &&
                sb[^4] == '\r' && sb[^3] == '\n' && sb[^2] == '\r' && sb[^1] == '\n')
            {
                return sb.ToString();
            }
        }
        throw new IOException("WebSocket handshake response headers were too large.");
    }
}
