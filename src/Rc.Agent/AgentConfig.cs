using System.Text.Json;

namespace Rc.Agent;

/// <summary>
/// Persisted agent configuration (<c>agent.config.json</c> next to the executable). Created with
/// sensible defaults on first run so the operator only has to edit the relay URL and token.
/// </summary>
public sealed class AgentConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string RelayUrl { get; set; } = "wss://relay.example.com/agent?id=endpoint-1";

    public string AgentId { get; set; } = "endpoint-1";

    public string Token { get; set; } = "CHANGE_ME";

    public bool AllowUntrustedCert { get; set; }

    /// <summary>
    /// Optional IP address to dial instead of resolving <see cref="RelayUrl"/>'s hostname.
    /// Use this when the network's DNS returns poisoned answers (e.g. a forced internal resolver):
    /// the URL keeps its hostname for SNI, but the TCP connection goes straight to this IP.
    /// </summary>
    public string ConnectIp { get; set; } = "";

    public string PinnedCertSha256 { get; set; } = "";

    public int TargetFps { get; set; } = 12;

    public int JpegQuality { get; set; } = 60;

    public int Scale { get; set; } = 100;

    public int TileSize { get; set; } = 64;

    public int KeyframeIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// When true the agent starts with screen sharing paused; the local user can resume it from the
    /// status window or the tray menu.
    /// </summary>
    public bool StartPaused { get; set; }

    /// <summary>
    /// When true the local status window is shown at startup. The tray icon is always present even
    /// when the window is hidden; "打开窗口" in the tray menu shows it on demand.
    /// </summary>
    public bool ShowWindow { get; set; } = true;

    public static string ConfigPath { get; } = Path.Combine(AppContext.BaseDirectory, "agent.config.json");

    /// <summary>Loads the config file, creating it with defaults when missing or unreadable.</summary>
    public static AgentConfig LoadOrCreate()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to read {ConfigPath}; falling back to defaults.", ex);
        }

        var created = new AgentConfig();
        created.Save();
        return created;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to write {ConfigPath}.", ex);
        }
    }

    /// <summary>Clamps values that would break capture or the wire protocol.</summary>
    public void Normalize()
    {
        TargetFps = Math.Clamp(TargetFps, 1, 60);
        JpegQuality = Math.Clamp(JpegQuality, 1, 100);
        Scale = Math.Clamp(Scale, 10, 100);
        TileSize = Math.Clamp(TileSize, 16, 512);
        KeyframeIntervalSeconds = Math.Clamp(KeyframeIntervalSeconds, 1, 3600);

        if (string.IsNullOrWhiteSpace(RelayUrl))
        {
            RelayUrl = "wss://relay.example.com";
        }
        if (string.IsNullOrWhiteSpace(AgentId))
        {
            AgentId = "agent";
        }

        ConnectIp = ConnectIp?.Trim() ?? "";
        PinnedCertSha256 = PinnedCertSha256?.Trim() ?? "";
    }

    /// <summary>
    /// Returns the effective endpoint. When <see cref="RelayUrl"/> carries no path we build
    /// <c>base + "/agent?id=" + agentId</c>; otherwise the configured URL is used verbatim.
    /// </summary>
    public string ResolveRelayUrl()
    {
        var raw = (RelayUrl ?? "").Trim();
        if (raw.Length == 0)
        {
            raw = "wss://relay.example.com";
        }
        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            raw = "wss://" + raw;
        }

        var uri = new Uri(raw, UriKind.Absolute);
        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            return uri.ToString();
        }

        var id = Uri.EscapeDataString(string.IsNullOrWhiteSpace(AgentId) ? "agent" : AgentId);
        var builder = new UriBuilder(uri)
        {
            Path = "/agent",
            Query = "id=" + id,
        };
        return builder.Uri.ToString();
    }
}
