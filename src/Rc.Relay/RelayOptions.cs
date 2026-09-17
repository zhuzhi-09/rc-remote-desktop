namespace Rc.Relay;

/// <summary>Relay configuration, bound from the "Relay" section of appsettings.json / environment.</summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>
    /// Shared secret both peers must send in the <c>X-RC-Token</c> header.
    /// When empty every WebSocket connection is rejected with 401.
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// Largest accepted message payload in bytes (the type byte is not counted).
    /// Default 64 MiB, matching the receive limit built into <c>Rc.Protocol.WsFraming</c>;
    /// larger values are silently capped by the protocol helper.
    /// </summary>
    public long MaxMessageBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Drop a session when a side has sent nothing for this many seconds.</summary>
    public int SessionTimeoutSeconds { get; set; } = 60;
}
