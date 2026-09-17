using System.Diagnostics;
using System.Net.WebSockets;
using Rc.Protocol;

namespace Rc.Controller;

/// <summary>
/// Owns the WebSocket link to the relay: connect/reconnect with exponential backoff (1s..30s),
/// protocol dispatch and thread-safe sends. All events are raised on background threads.
/// </summary>
internal sealed class RelayConnection : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Stopwatch _statsClock = Stopwatch.StartNew();

    private CancellationTokenSource? _cts;
    private WebSocket? _socket;
    private ControllerConfig? _config;
    private volatile bool _connected;
    private volatile bool _running;
    private long _framesReceived;
    private long _bytesReceived;

    public event Action? Connected;

    public event Action<string>? Disconnected;

    public event Action<HelloAckMessage>? HelloAckReceived;

    public event Action<FramePacket>? FrameReceived;

    public event Action<string>? ErrorOccurred;

    public event Action<string>? Log;

    /// <summary>Frames per second and bytes per second over the last sampling window.</summary>
    public event Action<double, double>? StatsUpdated;

    public bool IsConnected => _connected;

    public bool IsRunning => _running;

    /// <summary>Starts (or restarts) the connect/reconnect loop. Returns immediately.</summary>
    public void Start(ControllerConfig config)
    {
        Stop();
        _config = config.Snapshot();

        var cts = new CancellationTokenSource();
        lock (_stateLock)
        {
            _cts = cts;
            _running = true;
        }

        Task.Run(() => RunAsync(cts));
    }

    /// <summary>Cancels the loop and aborts the current socket.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        WebSocket? socket;
        lock (_stateLock)
        {
            cts = _cts;
            socket = _socket;
            _cts = null;
            _socket = null;
        }

        _connected = false;
        _running = false;

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            socket?.Abort();
        }
        catch (Exception ex)
        {
            Program.LogException(ex, "断开连接");
        }
    }

    public void SendInput(InputMessage message) => SendJson(MsgType.Input, message);

    public void SendControl(ControlMessage message) => SendJson(MsgType.Control, message);

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var backoff = TimeSpan.FromSeconds(1);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                WebSocket? socket = null;
                var reason = "连接已关闭";

                try
                {
                    var config = _config ?? throw new InvalidOperationException("尚未配置连接参数。");
                    Raise(() => Log?.Invoke($"正在连接 {config.RelayUrl} …"));

                    socket = await WsClient.ConnectAsync(config.ToWsOptions(), ct).ConfigureAwait(false);
                    lock (_stateLock)
                    {
                        _socket = socket;
                    }

                    var hello = new HelloMessage { Role = Role.Control, Version = ProtocolInfo.Version };
                    await SendRawAsync(socket, MsgType.Hello, ProtoJson.Serialize(hello), ct).ConfigureAwait(false);

                    backoff = TimeSpan.FromSeconds(1);
                    _connected = true;
                    Raise(() => Connected?.Invoke());

                    await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
                    reason = "连接已断开";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    reason = ex.Message;
                    Program.LogException(ex, "连接");
                    Raise(() => ErrorOccurred?.Invoke(ex.Message));
                }
                finally
                {
                    _connected = false;
                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_socket, socket))
                        {
                            _socket = null;
                        }
                    }

                    try
                    {
                        socket?.Abort();
                    }
                    catch
                    {
                    }

                    socket?.Dispose();
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                Raise(() => Disconnected?.Invoke(reason));
                Raise(() => Log?.Invoke($"将在 {backoff.TotalSeconds:0} 秒后自动重连…"));

                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 30_000));
            }
        }
        finally
        {
            lock (_stateLock)
            {
                // Only clear shared state when no newer session has taken over.
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                    _running = false;
                    _connected = false;
                }
            }

            cts.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await WsFraming.ReceiveAsync(socket, ct).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            var (type, payload) = message.Value;
            Interlocked.Add(ref _bytesReceived, payload.Length + 1);

            switch (type)
            {
                case MsgType.HelloAck:
                {
                    var ack = WsFraming.ReadJson<HelloAckMessage>(payload);
                    if (ack is not null)
                    {
                        if (!ack.Ok)
                        {
                            Raise(() => ErrorOccurred?.Invoke(ack.Error ?? "中转服务器拒绝了连接。"));
                        }

                        Raise(() => HelloAckReceived?.Invoke(ack));
                    }

                    break;
                }

                case MsgType.Frame:
                {
                    FramePacket packet;
                    try
                    {
                        packet = FrameCodec.Decode(payload);
                    }
                    catch (Exception ex)
                    {
                        Program.LogException(ex, "帧解码");
                        break;
                    }

                    Interlocked.Increment(ref _framesReceived);
                    Raise(() => FrameReceived?.Invoke(packet));
                    break;
                }

                case MsgType.Ping:
                    await SendRawAsync(socket, MsgType.Pong, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                    break;

                case MsgType.Error:
                {
                    var error = WsFraming.ReadJson<ErrorMessage>(payload);
                    Raise(() => ErrorOccurred?.Invoke(error?.Message ?? "远端返回了未知错误。"));
                    break;
                }

                default:
                    break;
            }

            PumpStats();
        }
    }

    private void PumpStats()
    {
        var elapsed = _statsClock.Elapsed;
        if (elapsed.TotalMilliseconds < 500)
        {
            return;
        }

        var frames = Interlocked.Exchange(ref _framesReceived, 0);
        var bytes = Interlocked.Exchange(ref _bytesReceived, 0);
        _statsClock.Restart();

        var fps = frames / elapsed.TotalSeconds;
        var bytesPerSecond = bytes / elapsed.TotalSeconds;
        Raise(() => StatsUpdated?.Invoke(fps, bytesPerSecond));
    }

    private void SendJson<T>(byte type, T value)
    {
        if (!_connected)
        {
            return;
        }

        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SendJsonLockedAsync(socket, type, value, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Program.LogException(ex, "发送消息");
            }
        });
    }

    private async Task SendJsonLockedAsync<T>(WebSocket socket, byte type, T value, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            await WsFraming.SendJsonAsync(socket, type, value, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendRawAsync(WebSocket socket, byte type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            await WsFraming.SendAsync(socket, type, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static void Raise(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Program.LogException(ex, "事件处理");
        }
    }
}
