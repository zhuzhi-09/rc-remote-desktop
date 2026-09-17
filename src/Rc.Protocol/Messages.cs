using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rc.Protocol;

/// <summary>Global protocol constants. Bump <see cref="Version"/> on breaking wire changes.</summary>
public static class ProtocolInfo
{
    public const int Version = 1;

    /// <summary>Side length (in pixels) of one screen tile used for dirty-region delta encoding.</summary>
    public const int DefaultTileSize = 64;
}

/// <summary>First byte of every WebSocket message identifies its kind.</summary>
public static class MsgType
{
    public const byte Hello = 1;
    public const byte HelloAck = 2;
    public const byte Frame = 3;
    public const byte Input = 4;
    public const byte Clipboard = 5;
    public const byte Control = 6;
    public const byte Ping = 7;
    public const byte Pong = 8;
    public const byte Error = 9;
}

public static class Role
{
    public const string Agent = "agent";
    public const string Control = "control";
}

/// <summary>JSON control channel helper. All control messages are UTF-8 JSON payloads.</summary>
public static class ProtoJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Options);
}

/// <summary>Sent by both peers immediately after the WebSocket upgrade.</summary>
public sealed class HelloMessage
{
    public string Role { get; set; } = Rc.Protocol.Role.Agent;
    public int Version { get; set; } = ProtocolInfo.Version;
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public int TileSize { get; set; } = ProtocolInfo.DefaultTileSize;
}

/// <summary>Relay's reply to <see cref="HelloMessage"/>.</summary>
public sealed class HelloAckMessage
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    /// <summary>True when both sides of the pair are connected.</summary>
    public bool PeerOnline { get; set; }

    /// <summary>Agent screen geometry, propagated to the controller.</summary>
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public int TileSize { get; set; } = ProtocolInfo.DefaultTileSize;
}

public static class InputKind
{
    public const string MouseMove = "mouse_move";
    public const string MouseDown = "mouse_down";
    public const string MouseUp = "mouse_up";
    public const string Wheel = "wheel";
    public const string KeyDown = "key_down";
    public const string KeyUp = "key_up";

    /// <summary>
    /// Inject literal text for <see cref="InputMessage.Text"/>. Needed because soft keyboards and
    /// IME-composed input (Chinese, emoji, non-US layouts) never produce Windows virtual-key codes.
    /// The agent injects it with KEYEVENTF_UNICODE.
    /// </summary>
    public const string Text = "text";
}

public static class MouseButton
{
    public const int Left = 0;
    public const int Right = 1;
    public const int Middle = 2;
}

/// <summary>Controller -> Agent input event. Mouse coordinates are normalized to 0..1.</summary>
public sealed class InputMessage
{
    public string Kind { get; set; } = "";

    /// <summary>Normalized X in [0,1] for <see cref="InputKind.MouseMove"/>.</summary>
    public double X { get; set; }

    /// <summary>Normalized Y in [0,1] for <see cref="InputKind.MouseMove"/>.</summary>
    public double Y { get; set; }

    /// <summary>One of <see cref="MouseButton"/>.</summary>
    public int Button { get; set; }

    /// <summary>Wheel delta (multiples of 120).</summary>
    public int Delta { get; set; }

    /// <summary>Windows virtual-key code.</summary>
    public int Vk { get; set; }

    /// <summary>True for extended keys (arrows, right ctrl/alt, numpad enter...).</summary>
    public bool Extended { get; set; }

    /// <summary>Literal text to type, used with <see cref="InputKind.Text"/>.</summary>
    public string? Text { get; set; }
}

public static class ControlKind
{
    /// <summary>Controller asks the agent to send a full keyframe.</summary>
    public const string RequestKeyframe = "request_keyframe";

    /// <summary>Set JPEG quality (1..100) in <see cref="ControlMessage.Value"/>.</summary>
    public const string SetQuality = "set_quality";

    /// <summary>Set capture scale percent (10..100) in <see cref="ControlMessage.Value"/>.</summary>
    public const string SetScale = "set_scale";
}

public sealed class ControlMessage
{
    public string Kind { get; set; } = "";
    public int Value { get; set; }
}

public sealed class ClipboardMessage
{
    public string? Text { get; set; }
}

public sealed class ErrorMessage
{
    public string? Message { get; set; }
}
