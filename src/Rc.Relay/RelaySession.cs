using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Rc.Protocol;

namespace Rc.Relay;

/// <summary>
/// One relay pair for a single agent id: at most one agent socket and one controller socket.
/// The session performs the Hello handshake, then forwards every message verbatim
/// (<c>[1 byte MsgType][payload]</c>) without re-decoding frames. Each direction has its own
/// bounded queue for backpressure, and the session reaps itself when either side goes away
/// or has been silent for <see cref="RelayOptions.SessionTimeoutSeconds"/>.
/// </summary>
public sealed class RelaySession
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    private readonly string _id;
    private readonly RelayOptions _options;
    private readonly RelayHub _hub;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    // Agent -> controller: frames are disposable snapshots, so overflow drops the oldest.
    private readonly Channel<WireMessage> _toController = Channel.CreateBounded<WireMessage>(
        new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    // Controller -> agent: discrete input/control events. Wait + TryWrite lets us log drops.
    private readonly Channel<WireMessage> _toAgent = Channel.CreateBounded<WireMessage>(
        new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly TaskCompletionSource<Peer> _agentReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Peer> _controllerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Peer? _agent;
    private Peer? _controller;
    private HelloMessage? _agentHello;
    private long _controllerDropped;
    private int _started;
    private int _ended;

    public RelaySession(string id, RelayOptions options, RelayHub hub, ILogger logger)
    {
        _id = id;
        _options = options;
        _hub = hub;
        _logger = logger;
    }

    public string Id => _id;

    /// <summary>
    /// Attaches an agent socket and starts driving it. Returns the connection lifetime task,
    /// or <c>null</c> when an agent is already attached (the caller rejects the newcomer).
    /// </summary>
    public Task? TryAttachAgent(WebSocket socket, CancellationToken requestAborted = default)
        => TryAttach(socket, isAgent: true, requestAborted);

    /// <summary>
    /// Attaches a controller socket and starts driving it. Returns the connection lifetime task,
    /// or <c>null</c> when a controller is already attached (the caller rejects the newcomer).
    /// </summary>
    public Task? TryAttachController(WebSocket socket, CancellationToken requestAborted = default)
        => TryAttach(socket, isAgent: false, requestAborted);

    private Task? TryAttach(WebSocket socket, bool isAgent, CancellationToken requestAborted)
    {
        var peer = new Peer(socket);
        lock (_gate)
        {
            if (Volatile.Read(ref _ended) != 0)
            {
                return null;
            }

            if (isAgent)
            {
                if (_agent is not null)
                {
                    return null;
                }

                _agent = peer;
            }
            else
            {
                if (_controller is not null)
                {
                    return null;
                }

                _controller = peer;
            }
        }

        EnsureStarted();
        return isAgent ? RunAgentAsync(peer, requestAborted) : RunControllerAsync(peer, requestAborted);
    }

    private void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() => PumpToControllerAsync(_cts.Token));
        _ = Task.Run(() => PumpToAgentAsync(_cts.Token));
        _ = Task.Run(() => HeartbeatAsync(_cts.Token));
    }

    private async Task RunAgentAsync(Peer peer, CancellationToken requestAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, requestAborted);
        var ct = linked.Token;
        var reason = "agent disconnected";

        try
        {
            var hello = await ReadHelloAsync(peer, ct).ConfigureAwait(false);
            if (hello is null)
            {
                reason = "agent handshake failed";
                return;
            }

            lock (_gate)
            {
                _agentHello = hello;
            }

            await peer.SendAsync(
                MsgType.HelloAck,
                ProtoJson.Serialize(new HelloAckMessage
                {
                    Ok = true,
                    PeerOnline = Volatile.Read(ref _controller) is not null,
                }),
                ct).ConfigureAwait(false);

            // Marks this peer's handshake as complete. Anything that must not overtake the first ack
            // (geometry updates, heartbeat pings) waits on Ready. Failing to complete it deadlocks
            // the peer's read loop, which shows up as a socket whose Recv-Q never drains.
            peer.Ready.TrySetResult();
            _agentReady.TrySetResult(peer);
            _logger.LogInformation(
                "session {Id}: agent online ({Width}x{Height}, tile {TileSize})",
                _id, hello.ScreenWidth, hello.ScreenHeight, hello.TileSize);

            // A controller waiting for this agent gets an updated ack carrying the geometry.
            // Deliberately NOT awaited: the agent's read loop must start immediately so frames are
            // drained from the socket, even if the controller is slow to finish its own handshake.
            var controller = Volatile.Read(ref _controller);
            if (controller is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await PublishGeometryAsync(controller, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "session {Id}: geometry update did not reach the controller", _id);
                    }
                }, CancellationToken.None);
            }

            await ReadLoopAsync(peer, _toController, logDroppedMessages: false, ct).ConfigureAwait(false);
            reason = "agent sent close";
        }
        catch (OperationCanceledException)
        {
            reason = "session closing";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session {Id}: agent loop failed", _id);
            reason = "agent error";
        }
        finally
        {
            await EndAsync(reason).ConfigureAwait(false);
        }
    }

    private async Task RunControllerAsync(Peer peer, CancellationToken requestAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, requestAborted);
        var ct = linked.Token;
        var reason = "controller disconnected";

        try
        {
            var hello = await ReadHelloAsync(peer, ct).ConfigureAwait(false);
            if (hello is null)
            {
                reason = "controller handshake failed";
                return;
            }

            HelloMessage? agentHello;
            lock (_gate)
            {
                agentHello = _agentHello;
            }

            await peer.SendAsync(
                MsgType.HelloAck,
                ProtoJson.Serialize(new HelloAckMessage
                {
                    Ok = true,
                    PeerOnline = agentHello is not null,
                    ScreenWidth = agentHello?.ScreenWidth ?? 0,
                    ScreenHeight = agentHello?.ScreenHeight ?? 0,
                    TileSize = agentHello?.TileSize ?? ProtocolInfo.DefaultTileSize,
                }),
                ct).ConfigureAwait(false);

            // See the matching comment in RunAgentAsync: Ready must be completed or the peer stalls.
            peer.Ready.TrySetResult();
            _controllerReady.TrySetResult(peer);
            _logger.LogInformation(
                "session {Id}: controller online (agent online: {AgentOnline})",
                _id, agentHello is not null);

            await ReadLoopAsync(peer, _toAgent, logDroppedMessages: true, ct).ConfigureAwait(false);
            reason = "controller sent close";
        }
        catch (OperationCanceledException)
        {
            reason = "session closing";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session {Id}: controller loop failed", _id);
            reason = "controller error";
        }
        finally
        {
            await EndAsync(reason).ConfigureAwait(false);
        }
    }

    private async Task<HelloMessage?> ReadHelloAsync(Peer peer, CancellationToken ct)
    {
        var received = await WsFraming.ReceiveAsync(peer.Socket, ct).ConfigureAwait(false);
        if (received is not { } message)
        {
            return null;
        }

        var (type, payload) = message;
        if (type != MsgType.Hello)
        {
            _logger.LogWarning("session {Id}: expected Hello, received msg type {Type}", _id, type);
            await ClosePeerAsync(peer, WebSocketCloseStatus.PolicyViolation, "expected Hello").ConfigureAwait(false);
            return null;
        }

        peer.Touch();
        try
        {
            return WsFraming.ReadJson<HelloMessage>(payload) ?? throw new JsonException("empty Hello");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "session {Id}: malformed Hello", _id);
            await ClosePeerAsync(peer, WebSocketCloseStatus.PolicyViolation, "malformed Hello").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Sends the controller a fresh HelloAck with the agent geometry. Waits for the controller's
    /// own handshake to finish so the update can never overtake its first ack.
    /// </summary>
    private async Task PublishGeometryAsync(Peer controller, CancellationToken ct)
    {
        await controller.Ready.Task.WaitAsync(ct).ConfigureAwait(false);

        HelloMessage? hello;
        lock (_gate)
        {
            hello = _agentHello;
        }

        if (hello is null)
        {
            return;
        }

        await controller.SendAsync(
            MsgType.HelloAck,
            ProtoJson.Serialize(new HelloAckMessage
            {
                Ok = true,
                PeerOnline = true,
                ScreenWidth = hello.ScreenWidth,
                ScreenHeight = hello.ScreenHeight,
                TileSize = hello.TileSize,
            }),
            ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(Peer source, Channel<WireMessage> target, bool logDroppedMessages, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var received = await WsFraming.ReceiveAsync(source.Socket, ct).ConfigureAwait(false);
            if (received is not { } message)
            {
                return;
            }

            var (type, payload) = message;
            source.Touch();

            if (payload.Length > _options.MaxMessageBytes)
            {
                _logger.LogWarning(
                    "session {Id}: message of {Size} bytes exceeds the {Limit} byte limit",
                    _id, payload.Length, _options.MaxMessageBytes);
                await ClosePeerAsync(source, WebSocketCloseStatus.MessageTooBig, "message too large").ConfigureAwait(false);
                return;
            }

            var wire = new WireMessage(type, payload);
            if (logDroppedMessages)
            {
                if (!target.Writer.TryWrite(wire))
                {
                    var dropped = Interlocked.Increment(ref _controllerDropped);
                    if (dropped == 1 || dropped % 100 == 0)
                    {
                        _logger.LogWarning(
                            "session {Id}: controller->agent queue full, {Dropped} messages dropped",
                            _id, dropped);
                    }
                }
            }
            else
            {
                // DropOldest never blocks; the newest frame is the one the controller needs.
                target.Writer.TryWrite(wire);
            }
        }
    }

    private async Task PumpToControllerAsync(CancellationToken ct)
    {
        try
        {
            var controller = await _controllerReady.Task.WaitAsync(ct).ConfigureAwait(false);
            await foreach (var message in _toController.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await controller.SendAsync(message.Type, message.Payload, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session {Id}: agent->controller pump failed", _id);
            await EndAsync("agent->controller pump failed").ConfigureAwait(false);
        }
    }

    private async Task PumpToAgentAsync(CancellationToken ct)
    {
        try
        {
            var agent = await _agentReady.Task.WaitAsync(ct).ConfigureAwait(false);
            await foreach (var message in _toAgent.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await agent.SendAsync(message.Type, message.Payload, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session {Id}: controller->agent pump failed", _id);
            await EndAsync("controller->agent pump failed").ConfigureAwait(false);
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(5, _options.SessionTimeoutSeconds));
        using var timer = new PeriodicTimer(PingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Peer? agent;
                Peer? controller;
                lock (_gate)
                {
                    agent = _agent;
                    controller = _controller;
                }

                if (IsSilent(agent, timeout))
                {
                    _logger.LogWarning("session {Id}: agent silent for over {Timeout}s", _id, timeout.TotalSeconds);
                    await EndAsync("agent idle timeout").ConfigureAwait(false);
                    return;
                }

                if (IsSilent(controller, timeout))
                {
                    _logger.LogWarning("session {Id}: controller silent for over {Timeout}s", _id, timeout.TotalSeconds);
                    await EndAsync("controller idle timeout").ConfigureAwait(false);
                    return;
                }

                await PingAsync(agent, ct).ConfigureAwait(false);
                await PingAsync(controller, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session {Id}: heartbeat failed", _id);
            await EndAsync("heartbeat failure").ConfigureAwait(false);
        }
    }

    private static bool IsSilent(Peer? peer, TimeSpan timeout) => peer is not null && peer.Idle > timeout;

    private async Task PingAsync(Peer? peer, CancellationToken ct)
    {
        if (peer is null || !peer.Ready.Task.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            await peer.SendAsync(MsgType.Ping, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "session {Id}: heartbeat ping failed", _id);
        }
    }

    private async Task EndAsync(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        _logger.LogInformation("session {Id}: closing ({Reason})", _id, reason);

        Peer? agent;
        Peer? controller;
        lock (_gate)
        {
            agent = _agent;
            controller = _controller;
        }

        await ClosePeerAsync(agent, WebSocketCloseStatus.NormalClosure, reason).ConfigureAwait(false);
        await ClosePeerAsync(controller, WebSocketCloseStatus.NormalClosure, reason).ConfigureAwait(false);

        _cts.Cancel();
        _toAgent.Writer.TryComplete();
        _toController.Writer.TryComplete();

        _hub.Detach(_id, this);
        _logger.LogInformation("session {Id}: removed", _id);
    }

    private async Task ClosePeerAsync(Peer? peer, WebSocketCloseStatus status, string reason)
    {
        if (peer is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            await peer.CloseAsync(status, reason, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "session {Id}: closing peer failed", _id);
        }
    }

    private readonly record struct WireMessage(byte Type, byte[] Payload);

    /// <summary>A socket plus the per-direction send serialization and activity tracking.</summary>
    private sealed class Peer
    {
        private long _lastActivity = Environment.TickCount64;

        public Peer(WebSocket socket) => Socket = socket;

        public WebSocket Socket { get; }

        /// <summary>WebSocket allows only one outstanding send; heartbeat, pumps and close share this gate.</summary>
        public SemaphoreSlim SendGate { get; } = new(1, 1);

        /// <summary>Completes once this peer's HelloAck has been sent.</summary>
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan Idle => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActivity));

        public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

        public async Task SendAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            await SendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Socket.State == WebSocketState.Open)
                {
                    await WsFraming.SendAsync(Socket, type, payload, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                SendGate.Release();
            }
        }

        public async Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken ct)
        {
            await SendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await Socket.CloseOutputAsync(status, description, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                SendGate.Release();
            }
        }
    }
}
