using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using Rc.Protocol;

namespace Rc.Relay;

/// <summary>
/// Singleton registry of live pairs: exactly one <see cref="RelaySession"/> per agent id,
/// each holding at most one agent and one controller.
/// </summary>
public sealed class RelayHub
{
    private readonly ConcurrentDictionary<string, RelaySession> _sessions = new(StringComparer.Ordinal);
    private readonly RelayOptions _options;
    private readonly ILogger<RelaySession> _sessionLogger;
    private readonly ILogger<RelayHub> _logger;

    public RelayHub(IOptions<RelayOptions> options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options.Value;
        _sessionLogger = loggerFactory.CreateLogger<RelaySession>();
        _logger = loggerFactory.CreateLogger<RelayHub>();
    }

    public int SessionCount => _sessions.Count;

    /// <summary>
    /// Attaches an agent to the session for <paramref name="id"/>.
    /// Returns the socket lifetime task, or <c>null</c> when a duplicate agent is present
    /// (the caller must close that socket with <see cref="WebSocketCloseStatus.PolicyViolation"/>).
    /// </summary>
    public Task? TryAttachAgent(string id, WebSocket socket, CancellationToken requestAborted = default)
        => Attach(id, socket, Role.Agent, requestAborted);

    /// <summary>
    /// Attaches a controller to the session for <paramref name="id"/>.
    /// Returns the socket lifetime task, or <c>null</c> when a duplicate controller is present
    /// (the caller must close that socket with <see cref="WebSocketCloseStatus.PolicyViolation"/>).
    /// </summary>
    public Task? TryAttachController(string id, WebSocket socket, CancellationToken requestAborted = default)
        => Attach(id, socket, Role.Control, requestAborted);

    /// <summary>Removes a finished session. Only removes the exact instance, never a replacement.</summary>
    public void Detach(string id, RelaySession session)
    {
        if (_sessions.TryRemove(new KeyValuePair<string, RelaySession>(id, session)))
        {
            _logger.LogInformation("Relay session {Id} removed ({Remaining} session(s) left).", id, _sessions.Count);
        }
    }

    private Task? Attach(string id, WebSocket socket, string role, CancellationToken requestAborted)
    {
        var session = _sessions.GetOrAdd(id, CreateSession);
        var lifetime = role == Role.Agent
            ? session.TryAttachAgent(socket, requestAborted)
            : session.TryAttachController(socket, requestAborted);

        if (lifetime is not null)
        {
            _logger.LogInformation("{Role} attached for id {Id}.", role, id);
        }

        return lifetime;
    }

    private RelaySession CreateSession(string id) => new(id, _options, this, _sessionLogger);
}
