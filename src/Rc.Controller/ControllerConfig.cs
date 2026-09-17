using System.Text.Json;
using System.Text.Json.Serialization;
using Rc.Protocol;

namespace Rc.Controller;

/// <summary>
/// Persisted controller settings (<c>controller.config.json</c>, next to the executable).
/// The file is created with defaults on first run.
/// </summary>
internal sealed class ControllerConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string RelayUrl { get; set; } = "wss://relay.example.com/control";

    public string AgentId { get; set; } = "";

    public string Token { get; set; } = "";

    public bool AllowUntrustedCert { get; set; }

    /// <summary>
    /// Optional IP address to dial instead of resolving <see cref="RelayUrl"/>'s hostname.
    /// Uses the URL's hostname for SNI and the Host header while dialing this address directly,
    /// so it still works when the network's DNS returns poisoned answers.
    /// </summary>
    public string ConnectIp { get; set; } = "";

    public string PinnedCertSha256 { get; set; } = "";

    public int JpegQuality { get; set; } = 60;

    public int Scale { get; set; } = 100;

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "controller.config.json");

    public static ControllerConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var fresh = new ControllerConfig();
                fresh.Save();
                return fresh;
            }

            var json = File.ReadAllText(ConfigPath);
            var loaded = JsonSerializer.Deserialize<ControllerConfig>(json, JsonOptions)
                         ?? throw new InvalidDataException("配置文件内容为空。");
            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex)
        {
            Program.LogException(ex, "读取配置");
            return new ControllerConfig();
        }
    }

    public void Save()
    {
        try
        {
            Normalize();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Program.LogException(ex, "保存配置");
        }
    }

    public void Normalize()
    {
        JpegQuality = Math.Clamp(JpegQuality, 10, 95);
        if (Scale is not (50 or 75 or 100))
        {
            Scale = 100;
        }

        RelayUrl = string.IsNullOrWhiteSpace(RelayUrl) ? "wss://relay.example.com/control" : RelayUrl.Trim();
        AgentId = AgentId?.Trim() ?? "";
        Token = Token?.Trim() ?? "";
        PinnedCertSha256 = PinnedCertSha256?.Trim() ?? "";
    }

    public ControllerConfig Snapshot() => new()
    {
        RelayUrl = RelayUrl,
        AgentId = AgentId,
        Token = Token,
        AllowUntrustedCert = AllowUntrustedCert,
        PinnedCertSha256 = PinnedCertSha256,
        JpegQuality = JpegQuality,
        Scale = Scale,
    };

    public WsClientOptions ToWsOptions() => new()
    {
        Url = BuildUrl(RelayUrl, AgentId),
        Token = string.IsNullOrWhiteSpace(Token) ? null : Token,
        AllowUntrustedCert = AllowUntrustedCert,
        PinnedCertSha256 = string.IsNullOrWhiteSpace(PinnedCertSha256) ? null : PinnedCertSha256,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>Appends (or replaces) the <c>id</c> query parameter that tells the relay which agent to pair with.</summary>
    public static string BuildUrl(string baseUrl, string agentId)
    {
        var trimmed = string.IsNullOrWhiteSpace(baseUrl) ? "wss://relay.example.com/control" : baseUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new UriFormatException($"中转地址无效：{trimmed}");
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return uri.ToString();
        }

        var query = uri.Query.TrimStart('?');
        var parts = new List<string>();
        if (query.Length > 0)
        {
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!part.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(part);
                }
            }
        }

        parts.Add("id=" + Uri.EscapeDataString(agentId.Trim()));
        return new UriBuilder(uri) { Query = string.Join("&", parts) }.Uri.ToString();
    }
}
