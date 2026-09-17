using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Rc.Protocol;
using Rc.Relay;

var builder = WebApplication.CreateBuilder(args);

// Console logging with wall-clock timestamps; the relay normally runs as a systemd/docker service.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});

// The relay is expected to sit behind Caddy/nginx/frp, so honour X-Forwarded-For / X-Forwarded-Proto.
// Trusting every proxy is intentional: keep the port reachable only from the proxy (firewall/bind address).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.SectionName));
builder.Services.AddSingleton<RelayHub>();

var app = builder.Build();

app.UseForwardedHeaders();

// REQUIRED: without this middleware ASP.NET Core does not recognise the upgrade request and
// context.WebSockets.IsWebSocketRequest is always false (AcceptWebSocketAsync would also fail
// because IHttpWebSocketFeature is only installed by this middleware).
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
});

var idPattern = new Regex(@"^[A-Za-z0-9_\-.]{1,64}$", RegexOptions.CultureInvariant);
var relayOptions = app.Services.GetRequiredService<IOptions<RelayOptions>>().Value;

if (string.IsNullOrWhiteSpace(relayOptions.Token) || relayOptions.Token == "CHANGE_ME")
{
    app.Logger.LogWarning(
        "Relay.Token is not configured (currently '{Token}'); every WebSocket connection will be rejected with 401.",
        relayOptions.Token);
}

if (relayOptions.MaxMessageBytes > 64L * 1024 * 1024)
{
    app.Logger.LogWarning(
        "Relay.MaxMessageBytes ({Max}) exceeds the 64 MiB receive limit built into Rc.Protocol; large messages will still be rejected.",
        relayOptions.MaxMessageBytes);
}

app.MapGet("/healthz", () => Results.Text("ok", "text/plain"));
app.MapGet("/agent", (HttpContext context) => HandleSocketAsync(context, Role.Agent, idPattern));
app.MapGet("/control", (HttpContext context) => HandleSocketAsync(context, Role.Control, idPattern));

// Mobile web client. Served from embedded resources so the single-file publish keeps working.
// NOTE: do NOT also map "/app/" - ASP.NET Core treats a trailing slash as equivalent, and two
// templates would collide with an AmbiguousMatchException at request time.
app.MapGet("/app", () => ServeWebAsset("index.html"));
app.MapGet("/app/app.js", () => ServeWebAsset("app.js"));
app.MapGet("/app/app.css", () => ServeWebAsset("app.css"));

app.Logger.LogInformation(
    "rcrelay ready: maxMessageBytes={MaxMessageBytes}, sessionTimeout={SessionTimeout}s, keepAlive=15s, ping=15s.",
    relayOptions.MaxMessageBytes, relayOptions.SessionTimeoutSeconds);

app.Run();

static async Task HandleSocketAsync(HttpContext context, string role, Regex idPattern)
{
    var services = context.RequestServices;
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Rc.Relay.Endpoint");
    var options = services.GetRequiredService<IOptions<RelayOptions>>().Value;
    var hub = services.GetRequiredService<RelayHub>();

    if (!context.WebSockets.IsWebSocketRequest)
    {
        logger.LogWarning(
            "Non-WebSocket request rejected: method={Method} proto={Protocol} upgrade='{Upgrade}' connection='{Connection}' wsKey='{WsKey}' wsVersion='{WsVersion}' path={Path}",
            context.Request.Method,
            context.Request.Protocol,
            context.Request.Headers.Upgrade.ToString(),
            context.Request.Headers.Connection.ToString(),
            context.Request.Headers["Sec-WebSocket-Key"].ToString(),
            context.Request.Headers["Sec-WebSocket-Version"].ToString(),
            context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("A WebSocket upgrade request is required.");
        return;
    }

    var id = context.Request.Query["id"].ToString();
    if (string.IsNullOrEmpty(id) || !idPattern.IsMatch(id))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Query parameter 'id' is required and must match ^[A-Za-z0-9_\\-.]{1,64}$.");
        return;
    }

    // Browsers cannot set custom headers on a WebSocket, so the web client passes the token as a
    // query parameter. Both forms are accepted; the header takes precedence.
    var providedToken = context.Request.Headers["X-RC-Token"].ToString();
    if (string.IsNullOrEmpty(providedToken))
    {
        providedToken = context.Request.Query["token"].ToString();
    }

    if (!TokenMatches(providedToken, options.Token))
    {
        logger.LogWarning("Rejected {Role} (id={Id}, remote={Remote}): bad or missing token.", role, id, context.Connection.RemoteIpAddress);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Invalid or missing token.");
        return;
    }

    // Disable Nagle so input events and small control messages are not delayed.
    context.Features.Get<IConnectionSocketFeature>()?.Socket.NoDelay = true;

    using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
    {
        KeepAliveInterval = TimeSpan.FromSeconds(15),
    });

    logger.LogInformation("{Role} connected: id={Id} remote={Remote}", role, id, context.Connection.RemoteIpAddress);

    var lifetime = role == Role.Agent
        ? hub.TryAttachAgent(id, socket, context.RequestAborted)
        : hub.TryAttachController(id, socket, context.RequestAborted);

    if (lifetime is null)
    {
        logger.LogWarning("Rejected duplicate {Role} for id={Id}.", role, id);
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, $"duplicate {role}", CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The peer vanished before we could tell it why.
        }

        return;
    }

    await lifetime;
}

static bool TokenMatches(string? provided, string expected)
{
    if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
    {
        return false;
    }

    // Constant-time comparison; length is the only thing that leaks, which is unavoidable.
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(provided),
        Encoding.UTF8.GetBytes(expected));
}

/// <summary>Serves one file of the embedded mobile web client.</summary>
static IResult ServeWebAsset(string fileName)
{
    var assembly = Assembly.GetExecutingAssembly();
    var suffix = "." + fileName;
    var resource = assembly.GetManifestResourceNames()
        .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    if (resource is null)
    {
        return Results.NotFound();
    }

    var contentType = Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        _ => "text/html; charset=utf-8",
    };

    return Results.Stream(assembly.GetManifestResourceStream(resource)!, contentType);
}
