using System.Net.WebSockets;
using System.Threading.Channels;
using Rc.Protocol;

namespace Rc.Agent;

/// <summary>
/// Owns the outbound WebSocket session: connect, handshake, then concurrent send / receive / ping
/// loops. Reconnects with exponential backoff (1 s doubling to 60 s). Frames are handed over through
/// a single-slot channel so a slow network can never grow an unbounded queue.
/// </summary>
public sealed class AgentConnection : IDisposable
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(45);

    private readonly AgentConfig _config;
    private readonly Func<(int Width, int Height)> _geometry;
    private readonly Channel<FramePacket> _frames = Channel.CreateBounded<FramePacket>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _attemptGate = new();

    private CancellationTokenSource? _attemptCts;
    private Task? _runTask;
    private volatile bool _reconnectRequested;
    private long _lastPongTicks;

    public AgentConnection(AgentConfig config, Func<(int Width, int Height)> geometry)
    {
        _config = config;
        _geometry = geometry;
    }

    /// <summary>Raised whenever the connection state changes; the tray uses this for its status line.</summary>
    public event Action<string>? StateChanged;

    /// <summary>Controller asked for a full keyframe.</summary>
    public event Action? KeyframeRequested;

    /// <summary>Controller set a new JPEG quality (1..100).</summary>
    public event Action<int>? QualityRequested;

    /// <summary>Controller set a new capture scale (10..100).</summary>
    public event Action<int>? ScaleRequested;

    public void Start() => _runTask = Task.Run(() => ReconnectLoopAsync(_disposeCts.Token));

    /// <summary>Drops the current session and reconnects immediately.</summary>
    public void Reconnect()
    {
        _reconnectRequested = true;
        lock (_attemptGate)
        {
            try
            {
                _attemptCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The attempt finished between the read and the cancel; nothing to do.
            }
        }
    }

    /// <summary>Queues a frame. When the sender is still busy the previous pending frame is dropped.</summary>
    public void PostFrame(FramePacket frame) => _frames.Writer.TryWrite(frame);

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        SetState("Connecting");

        while (!ct.IsCancellationRequested)
        {
            _reconnectRequested = false;
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_attemptGate)
            {
                _attemptCts = attemptCts;
            }

            var connected = false;
            try
            {
                connected = await RunOnceAsync(attemptCts.Token).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_reconnectRequested)
                {
                    Log.Warn($"Connection attempt failed: {ex.Message}");
                }
            }
            finally
            {
                lock (_attemptGate)
                {
                    _attemptCts = null;
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            SetState("Reconnecting");
            if (_reconnectRequested)
            {
                continue;
            }

            if (!connected)
            {
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetState("Stopped");
    }

    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var (width, height) = _geometry();
        var url = _config.ResolveRelayUrl();

        var options = new WsClientOptions
        {
            Url = url,
            Token = string.IsNullOrEmpty(_config.Token) ? null : _config.Token,
            AllowUntrustedCert = _config.AllowUntrustedCert,
            ConnectAddress = string.IsNullOrWhiteSpace(_config.ConnectIp) ? null : _config.ConnectIp.Trim(),
            PinnedCertSha256 = string.IsNullOrEmpty(_config.PinnedCertSha256) ? null : _config.PinnedCertSha256,
        };

        using var socket = await WsClient.ConnectAsync(options, ct).ConfigureAwait(false);

        var hello = new HelloMessage
        {
            Role = Rc.Protocol.Role.Agent,
            Version = ProtocolInfo.Version,
            ScreenWidth = width,
            ScreenHeight = height,
            TileSize = _config.TileSize,
        };
        await WsFraming.SendJsonAsync(socket, MsgType.Hello, hello, ct).ConfigureAwait(false);

        var reply = await WsFraming.ReceiveAsync(socket, ct).ConfigureAwait(false);
        if (reply is null)
        {
            throw new IOException("Relay closed the connection during the handshake.");
        }
        if (reply.Value.Type != MsgType.HelloAck)
        {
            throw new InvalidDataException($"Expected HelloAck but received message type {reply.Value.Type}.");
        }

        var ack = WsFraming.ReadJson<HelloAckMessage>(reply.Value.Payload);
        if (ack is null || !ack.Ok)
        {
            throw new InvalidOperationException($"Relay rejected the agent: {ack?.Error ?? "unknown error"}");
        }

        Log.Info($"Connected to {url} (peerOnline={ack.PeerOnline}).");
        SetState("Connected");
        Volatile.Write(ref _lastPongTicks, Environment.TickCount64);

        // Drop any frame that was queued while the previous session was down; a fresh keyframe
        // is requested below and will supersede it.
        while (_frames.Reader.TryRead(out _))
        {
        }

        KeyframeRequested?.Invoke();

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = SendLoopAsync(socket, loopCts.Token);
        var receive = ReceiveLoopAsync(socket, loopCts.Token);
        var ping = PingLoopAsync(socket, loopCts.Token);

        await Task.WhenAny(send, receive, ping).ConfigureAwait(false);
        loopCts.Cancel();

        try
        {
            await Task.WhenAll(send, receive, ping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when one loop ends and tears the others down.
        }
        catch (Exception ex)
        {
            Log.Warn($"Session ended: {ex.Message}");
        }

        return true;
    }

    private async Task SendLoopAsync(WebSocket socket, CancellationToken ct)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var payload = FrameCodec.Encode(frame);
            await WsFraming.SendAsync(socket, MsgType.Frame, payload, ct).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await WsFraming.ReceiveAsync(socket, ct).ConfigureAwait(false);
            if (message is null)
            {
                Log.Info("Relay closed the connection.");
                return;
            }

            var (type, payload) = message.Value;
            switch (type)
            {
                case MsgType.Input:
                {
                    var input = WsFraming.ReadJson<InputMessage>(payload);
                    if (input is not null)
                    {
                        Inject(input);
                    }
                    break;
                }
                case MsgType.Control:
                {
                    var control = WsFraming.ReadJson<ControlMessage>(payload);
                    if (control is not null)
                    {
                        HandleControl(control);
                    }
                    break;
                }
                case MsgType.Ping:
                    await WsFraming.SendAsync(socket, MsgType.Pong, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                    break;
                case MsgType.Pong:
                    Volatile.Write(ref _lastPongTicks, Environment.TickCount64);
                    break;
                case MsgType.Clipboard:
                    Log.Info("Ignoring clipboard message (not supported by this agent build).");
                    break;
                case MsgType.Error:
                {
                    var error = WsFraming.ReadJson<ErrorMessage>(payload);
                    Log.Warn($"Relay error: {error?.Message ?? "unspecified"}");
                    break;
                }
                default:
                    Log.Warn($"Ignoring unknown message type {type}.");
                    break;
            }
        }
    }

    private async Task PingLoopAsync(WebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PingInterval, ct).ConfigureAwait(false);

            if (Environment.TickCount64 - Volatile.Read(ref _lastPongTicks) > PongTimeout.TotalMilliseconds)
            {
                Log.Warn($"No Pong for {PongTimeout.TotalSeconds:0}s; treating the connection as dead.");
                return;
            }

            await WsFraming.SendAsync(socket, MsgType.Ping, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        }
    }

    private static void Inject(InputMessage message)
    {
        try
        {
            switch (message.Kind)
            {
                case InputKind.MouseMove:
                    InputInjector.MoveAbsolute(message.X, message.Y);
                    break;
                case InputKind.MouseDown:
                    InputInjector.MouseDown(message.Button);
                    break;
                case InputKind.MouseUp:
                    InputInjector.MouseUp(message.Button);
                    break;
                case InputKind.Wheel:
                    InputInjector.Wheel(message.Delta);
                    break;
                case InputKind.KeyDown:
                    InputInjector.Key(message.Vk, down: true, message.Extended);
                    break;
                case InputKind.KeyUp:
                    InputInjector.Key(message.Vk, down: false, message.Extended);
                    break;
                default:
                    Log.Warn($"Unknown input kind '{message.Kind}'.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Input injection failed.", ex);
        }
    }

    private void HandleControl(ControlMessage message)
    {
        switch (message.Kind)
        {
            case ControlKind.RequestKeyframe:
                KeyframeRequested?.Invoke();
                break;
            case ControlKind.SetQuality:
                QualityRequested?.Invoke(Math.Clamp(message.Value, 1, 100));
                break;
            case ControlKind.SetScale:
                ScaleRequested?.Invoke(Math.Clamp(message.Value, 10, 100));
                break;
            default:
                Log.Warn($"Unknown control kind '{message.Kind}'.");
                break;
        }
    }

    private void SetState(string state) => StateChanged?.Invoke(state);

    public void Dispose()
    {
        try
        {
            _disposeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        _frames.Writer.TryComplete();
        try
        {
            _runTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Shutdown race; the process is exiting anyway.
        }

        _disposeCts.Dispose();
    }
}
