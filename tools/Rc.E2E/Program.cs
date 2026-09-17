using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Rc.Protocol;

namespace Rc.E2E;

/// <summary>
/// End-to-end smoke test: boots a real relay + a real agent in a temp directory, connects as a
/// controller over the real wire protocol, and asserts that the handshake, frame delta encoding and
/// keyframe-on-demand paths all work.
///
/// It deliberately does NOT send mouse/keyboard input, because that would move the cursor and type
/// into whatever window is focused on the machine running the test. Pass --test-input to enable it
/// when you are prepared for that.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var opt = Options.Parse(args);
        var repoRoot = FindRepoRoot();
        var relayDir = opt.RelayDir ?? Path.Combine(repoRoot, "src", "Rc.Relay", "bin", "Debug", "net10.0");
        var agentDir = opt.AgentDir ?? Path.Combine(repoRoot, "src", "Rc.Agent", "bin", "Debug", "net10.0-windows");
        var workDir = Path.Combine(Path.GetTempPath(), "rce2e-" + Guid.NewGuid().ToString("N")[..8]);
        var agentWorkDir = Path.Combine(workDir, "agent");

        Process? relay = null;
        Process? agent = null;
        Process? agentCf = null;
        var relayLog = new StringBuilder();
        var agentLog = new StringBuilder();
        var results = new List<(bool Ok, string Name, string Detail)>();

        try
        {
            foreach (var required in new[]
                     {
                         Path.Combine(relayDir, "rcrelay.dll"),
                         Path.Combine(agentDir, "rcagent.exe"),
                     })
            {
                if (!File.Exists(required))
                {
                    Console.Error.WriteLine($"Missing build output: {required}");
                    Console.Error.WriteLine("Run: dotnet build RemoteControl.slnx");
                    return 2;
                }
            }

            var scheme = opt.Tls ? "wss" : "ws";
            var relayEnv = new Dictionary<string, string>
            {
                ["Relay__Token"] = opt.Token,
                ["Logging__LogLevel__Default"] = "Information",
            };
            string? pinnedFingerprint = null;
            if (opt.Tls)
            {
                string pfxPath;
                string pfxPassword;
                if (!string.IsNullOrWhiteSpace(opt.PfxPath))
                {
                    if (string.IsNullOrWhiteSpace(opt.Pin))
                    {
                        results.Add((false, "test certificate", "--pfx requires --pin (the value the deploy script printed)"));
                        return Report(results, relayLog, agentLog, workDir, opt.Keep);
                    }

                    // Crucially we use the CLI-supplied fingerprint verbatim. If it does not match what
                    // Rc.Protocol computes from the same certificate, the handshake fails and this test
                    // catches the mismatch before it ever reaches production.
                    pfxPath = opt.PfxPath;
                    pfxPassword = opt.PfxPassword ?? "";
                    pinnedFingerprint = opt.Pin;
                    results.Add((true, "test certificate",
                        $"external PFX {Path.GetFileName(pfxPath)}, openssl pin={Preview(opt.Pin)}"));
                }
                else
                {
                    var generated = CreateSelfSignedCert(workDir);
                    pfxPath = generated.PfxPath;
                    pfxPassword = generated.Password;
                    pinnedFingerprint = generated.Sha256;
                    results.Add((true, "test certificate", $"self-signed, sha256={generated.Sha256[..16]}..."));
                }

                // Reuse the "Http" endpoint name but give it an https URL + certificate.
                relayEnv["Kestrel__Endpoints__Http__Url"] = $"https://127.0.0.1:{opt.Port}";
                relayEnv["Kestrel__Endpoints__Http__Certificate__Path"] = pfxPath;
                relayEnv["Kestrel__Endpoints__Http__Certificate__Password"] = pfxPassword;
            }
            else
            {
                // NOTE: appsettings.json defines Kestrel:Endpoints, which takes precedence over
                // ASPNETCORE_URLS. Overriding the same configuration key is the only way to move the port.
                relayEnv["Kestrel__Endpoints__Http__Url"] = $"http://127.0.0.1:{opt.Port}";
            }

            Directory.CreateDirectory(agentWorkDir);
            CopyDirectory(agentDir, agentWorkDir);

            await File.WriteAllTextAsync(Path.Combine(agentWorkDir, "agent.config.json"), JsonSerializer.Serialize(new
            {
                relayUrl = $"{scheme}://127.0.0.1:{opt.Port}/agent?id={opt.AgentId}",
                agentId = opt.AgentId,
                token = opt.Token,
                allowUntrustedCert = false,
                pinnedCertSha256 = pinnedFingerprint ?? "",
                targetFps = 15,
                jpegQuality = 60,
                scale = 100,
                tileSize = 64,
                keyframeIntervalSeconds = 2,
                // No window during automated runs: tests must never pop up UI, and a headless CI
                // runner may not be able to create one at all.
                showWindow = false,
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

            relay = Start("dotnet", relayDir, relayLog, new[] { Path.Combine(relayDir, "rcrelay.dll") }, relayEnv);

            var healthy = await WaitForHealthAsync(opt.Port, scheme == "wss");
            results.Add((healthy, "relay /healthz", healthy ? "ok" : "no response within 15s"));
            if (!healthy)
            {
                return Report(results, relayLog, agentLog, workDir, opt.Keep);
            }

            // No --console: the agent writes agent.log next to the exe, so we avoid popping a console window.
            agent = Start(Path.Combine(agentWorkDir, "rcagent.exe"), agentWorkDir, agentLog, [], null);
            await Task.Delay(1500);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var url = $"{scheme}://127.0.0.1:{opt.Port}/control?id={opt.AgentId}";
            using var ws = new WebSocketHandle(await WsClient.ConnectAsync(new WsClientOptions
            {
                Url = url,
                Token = opt.Token,
                AllowUntrustedCert = false,
                PinnedCertSha256 = pinnedFingerprint,
            }, cts.Token));

            await WsFraming.SendJsonAsync(ws.Socket, MsgType.Hello, new HelloMessage { Role = Role.Control }, cts.Token);

            var ack = await WaitForHelloAckAsync(ws.Socket, TimeSpan.FromSeconds(20));
            var ackOk = ack is { Ok: true, ScreenWidth: > 0, ScreenHeight: > 0, TileSize: 64 };
            results.Add((ackOk, "controller handshake",
                ack is null
                    ? "no HelloAck with agent geometry within 20s"
                    : $"Ok={ack.Ok} {ack.ScreenWidth}x{ack.ScreenHeight} tile={ack.TileSize} peerOnline={ack.PeerOnline}"));

            var firstKeyframe = await WaitForKeyframeAsync(ws.Socket, TimeSpan.FromSeconds(20));
            if (firstKeyframe is null)
            {
                results.Add((false, "first keyframe", "no keyframe within 20s (agent did not capture/encode?)"));
            }
            else
            {
                var (packet, problem) = ValidatePacket(firstKeyframe, ack);
                results.Add((problem is null, "first keyframe",
                    problem ?? $"{packet.Tiles.Count} tiles, {packet.FrameWidth}x{packet.FrameHeight}, frameId={packet.FrameId}"));
            }

            await WsFraming.SendJsonAsync(ws.Socket, MsgType.Control,
                new ControlMessage { Kind = ControlKind.RequestKeyframe }, cts.Token);

            var idBefore = firstKeyframe?.FrameId ?? 0;
            var nextKeyframe = await WaitForKeyframeAsync(ws.Socket, TimeSpan.FromSeconds(15), idBefore);
            results.Add((nextKeyframe is not null, "keyframe on demand",
                nextKeyframe is null
                    ? "agent did not answer request_keyframe within 15s"
                    : $"frameId {idBefore} -> {nextKeyframe.FrameId}"));

            // Input path check: controller -> relay -> agent -> SendInput.
            // By default we re-send the cursor's CURRENT position, so nothing visibly moves while we
            // still exercise the whole injection chain. --test-input moves it to the screen centre.
            var target = opt.TestInput ? (X: 0.5, Y: 0.5) : CurrentNormalizedCursor();
            if (target is null)
            {
                results.Add((false, "input path", "could not read the cursor position"));
            }
            else
            {
                await WsFraming.SendJsonAsync(ws.Socket, MsgType.Input, new InputMessage
                {
                    Kind = InputKind.MouseMove,
                    X = target.Value.X,
                    Y = target.Value.Y,
                }, cts.Token);
                await Task.Delay(600);
                var socketOpen = ws.Socket.State == WebSocketState.Open;
                var clean = ReadAgentErrors(agentWorkDir) is null;
                results.Add((socketOpen && clean, "input path",
                    $"mouse_move injected ({(opt.TestInput ? "cursor moved to centre" : "cursor unchanged")}), socket={ws.Socket.State}, agentClean={clean}"));
            }

            // Text injection plumbing (the mobile web client depends on InputKind.Text).
            // By default we send an EMPTY string, which the agent deliberately ignores: that proves
            // the message is parsed and routed without typing anything into whatever window happens
            // to be focused on the machine running the tests. --test-input sends real characters.
            var probeText = opt.TestInput ? "rc" : "";
            await WsFraming.SendJsonAsync(ws.Socket, MsgType.Input, new InputMessage
            {
                Kind = InputKind.Text,
                Text = probeText,
            }, cts.Token);
            await Task.Delay(400);
            var textClean = ReadAgentErrors(agentWorkDir) is null;
            results.Add((ws.Socket.State == WebSocketState.Open && textClean, "text input plumbing",
                opt.TestInput
                    ? "typed \"rc\" - check the focused window"
                    : $"empty text accepted, nothing typed, agentClean={textClean}"));

            // MUST stay last among the checks that use this socket: cancelling a pending
            // WebSocket.ReceiveAsync aborts the connection (ManagedWebSocket behaviour), so after this
            // point the socket can no longer be used.
            var frameCount = await CountFramesAsync(ws.Socket, TimeSpan.FromSeconds(4));
            results.Add((frameCount > 0, "frame stream alive", $"{frameCount} frame message(s) in 4s"));

            if (opt.Tls)
            {
                var rejected = false;
                try
                {
                    using var bogus = await WsClient.ConnectAsync(new WsClientOptions
                    {
                        Url = url,
                        Token = opt.Token,
                        PinnedCertSha256 = new string('A', 64),
                        ConnectTimeout = TimeSpan.FromSeconds(5),
                    }, CancellationToken.None);
                }
                catch (Exception)
                {
                    rejected = true;
                }

                results.Add((rejected, "cert pinning enforced",
                    rejected ? "a wrong pinned fingerprint was rejected" : "SECURITY FAILURE: a wrong pin was accepted"));
            }

            var agentErrors = ReadAgentErrors(agentWorkDir);
            results.Add((agentErrors is null, "agent log clean", agentErrors ?? "no errors logged"));

            // ---- controller-first regression phase ----
            // Every check above connects the agent first. This phase covers the reversed attach order
            // that once exposed a real bug: when a controller was already attached and an agent then
            // joined that existing session, RelaySession.RunAgentAsync called PublishGeometryAsync
            // before starting the agent read loop. That call awaited controller.Ready, which was never
            // completed, so the agent socket was never read and the controller stayed frozen forever.
            // The fix completes peer.Ready after the HelloAck; this test fails if that regresses.
            if (!opt.SkipControllerFirst)
            {
                var cfId = opt.AgentId + "-cf";
                var agentCfWorkDir = Path.Combine(workDir, "agent-cf");
                var cfOnlineRecorded = false;
                try
                {
                    // Same agent payload as the first phase, but a different id gets a fresh session.
                    CopyDirectory(agentDir, agentCfWorkDir);
                    await File.WriteAllTextAsync(Path.Combine(agentCfWorkDir, "agent.config.json"), JsonSerializer.Serialize(new
                    {
                        relayUrl = $"{scheme}://127.0.0.1:{opt.Port}/agent?id={cfId}",
                        agentId = cfId,
                        token = opt.Token,
                        allowUntrustedCert = false,
                        pinnedCertSha256 = pinnedFingerprint ?? "",
                        targetFps = 15,
                        jpegQuality = 60,
                        scale = 100,
                        tileSize = 64,
                        keyframeIntervalSeconds = 2,
                        showWindow = false,
                    }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

                    using var cfCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    using var cfWs = new WebSocketHandle(await WsClient.ConnectAsync(new WsClientOptions
                    {
                        Url = $"{scheme}://127.0.0.1:{opt.Port}/control?id={cfId}",
                        Token = opt.Token,
                        AllowUntrustedCert = false,
                        PinnedCertSha256 = pinnedFingerprint,
                    }, cfCts.Token));

                    await WsFraming.SendJsonAsync(cfWs.Socket, MsgType.Hello, new HelloMessage { Role = Role.Control }, cfCts.Token);

                    // Controller only: the relay must answer with an Ok ack carrying no agent geometry.
                    var cfAck = await WaitForHelloAckAsync(cfWs.Socket, TimeSpan.FromSeconds(20), requireGeometry: false);
                    cfOnlineRecorded = true;
                    var cfOnlineOk = cfAck is { Ok: true, PeerOnline: false, ScreenWidth: 0 };
                    results.Add((cfOnlineOk, "controller-first: controller online with no agent",
                        cfAck is null
                            ? "no HelloAck within 20s"
                            : $"Ok={cfAck.Ok} peerOnline={cfAck.PeerOnline} screen={cfAck.ScreenWidth}x{cfAck.ScreenHeight}"));

                    // Let the session settle so the agent really joins an already-established session.
                    await Task.Delay(1500, cfCts.Token);

                    // rcagent enforces a single instance via a machine-wide named mutex, so the first
                    // agent must be gone before this one can start. All of its checks have completed.
                    TryKill(agent);
                    agent = null;

                    agentCf = Start(Path.Combine(agentCfWorkDir, "rcagent.exe"), agentCfWorkDir, agentLog, [], null);

                    var cfStart = Stopwatch.StartNew();
                    var cfKeyframe = await WaitForKeyframeAsync(cfWs.Socket, TimeSpan.FromSeconds(25));
                    if (cfKeyframe is null)
                    {
                        results.Add((false, "controller-first: agent joins existing session",
                            "no keyframe within 25s (agent socket likely never read)"));
                    }
                    else
                    {
                        // Pass a null ack: the only ack this socket saw predates the agent, so its 0x0
                        // geometry must not be cross-checked against the real frame size.
                        var (_, problem) = ValidatePacket(cfKeyframe, null);
                        results.Add((problem is null, "controller-first: agent joins existing session",
                            problem ?? $"keyframe after {cfStart.ElapsedMilliseconds}ms, {cfKeyframe.Tiles.Count} tiles"));
                    }
                }
                catch (Exception ex)
                {
                    // Record the missing results instead of letting the phase abort the whole run.
                    if (!cfOnlineRecorded)
                    {
                        results.Add((false, "controller-first: controller online with no agent",
                            ex.GetType().Name + ": " + ex.Message));
                    }
                    results.Add((false, "controller-first: agent joins existing session", ex.GetType().Name + ": " + ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            results.Add((false, "harness", ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            TryKill(agent);
            TryKill(agentCf);
            TryKill(relay);
        }

        return Report(results, relayLog, agentLog, workDir, opt.Keep);
    }

    private sealed record WebSocketHandle(WebSocket Socket) : IDisposable
    {
        public void Dispose()
        {
            try { Socket.Abort(); } catch { /* best effort */ }
            Socket.Dispose();
        }
    }

    /// <summary>Normalized position of the real cursor, so the input test can be a visual no-op.</summary>
    private static (double X, double Y)? CurrentNormalizedCursor()
    {
        if (!GetCursorPos(out var point))
        {
            return null;
        }
        var width = GetSystemMetrics(SM_CXSCREEN);
        var height = GetSystemMetrics(SM_CYSCREEN);
        if (width <= 0 || height <= 0)
        {
            return null;
        }
        return (Math.Clamp(point.X / (double)width, 0, 1), Math.Clamp(point.Y / (double)height, 0, 1));
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private static string Preview(string hex) => hex.Length <= 16 ? hex : hex[..16] + "...";

    private static async Task<bool> WaitForHealthAsync(int port, bool insecure)
    {
        using var handler = new HttpClientHandler();
        if (insecure)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var scheme = insecure ? "https" : "http";
        for (var i = 0; i < 75; i++)
        {
            try
            {
                var response = await http.GetAsync($"{scheme}://127.0.0.1:{port}/healthz");
                if (response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Trim() == "ok")
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // relay still starting up
            }
            await Task.Delay(200);
        }
        return false;
    }

    /// <summary>Creates a throwaway self-signed server certificate so the wss:// + pinning path is exercised.</summary>
    private static (string PfxPath, string Password, string Sha256) CreateSelfSignedCert(string dir)
    {
        Directory.CreateDirectory(dir);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        const string password = "e2e";
        var pfxPath = Path.Combine(dir, "relay.pfx");
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, password));

        return (pfxPath, password, Convert.ToHexString(SHA256.HashData(cert.RawData)));
    }

    /// <summary>
    /// Waits for a HelloAck. <paramref name="requireGeometry"/> is true for the normal agent-first
    /// flow (the ack must carry the agent geometry); false for controller-first, where the first ack
    /// legitimately reports a zero-sized screen because no agent has joined yet.
    /// </summary>
    private static async Task<HelloAckMessage?> WaitForHelloAckAsync(WebSocket ws, TimeSpan timeout, bool requireGeometry = true)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReceiveAsync(ws, deadline - DateTime.UtcNow);
            if (message is null)
            {
                return null;
            }
            if (message.Value.Type == MsgType.HelloAck)
            {
                var ack = WsFraming.ReadJson<HelloAckMessage>(message.Value.Payload);
                if (ack is not null && (!requireGeometry || ack.ScreenWidth > 0))
                {
                    return ack;
                }
            }
        }
        return null;
    }

    private static async Task<FramePacket?> WaitForKeyframeAsync(WebSocket ws, TimeSpan timeout, uint minFrameId = 0)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReceiveAsync(ws, deadline - DateTime.UtcNow);
            if (message is null)
            {
                return null;
            }
            if (message.Value.Type != MsgType.Frame)
            {
                continue;
            }
            try
            {
                var packet = FrameCodec.Decode(message.Value.Payload);
                if (packet.Keyframe && packet.FrameId > minFrameId && packet.Tiles.Count > 0)
                {
                    return packet;
                }
            }
            catch (InvalidDataException)
            {
                // ignore malformed frame; the validator will surface it separately
            }
        }
        return null;
    }

    private static async Task<int> CountFramesAsync(WebSocket ws, TimeSpan window)
    {
        var count = 0;
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReceiveAsync(ws, deadline - DateTime.UtcNow);
            if (message is null)
            {
                break;
            }
            if (message.Value.Type == MsgType.Frame)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Returns null when the packet is well-formed, otherwise a description of the defect.</summary>
    private static (FramePacket Packet, string? Problem) ValidatePacket(FramePacket packet, HelloAckMessage? ack)
    {
        if (packet.FrameWidth <= 0 || packet.FrameHeight <= 0)
        {
            return (packet, $"bad frame geometry {packet.FrameWidth}x{packet.FrameHeight}");
        }
        if (packet.TileSize != 64)
        {
            return (packet, $"unexpected tile size {packet.TileSize}");
        }
        if (packet.Tiles.Count == 0)
        {
            return (packet, "keyframe carried zero tiles");
        }
        if (ack is not null && (packet.FrameWidth != ack.ScreenWidth || packet.FrameHeight != ack.ScreenHeight))
        {
            return (packet, $"frame geometry {packet.FrameWidth}x{packet.FrameHeight} disagrees with HelloAck {ack.ScreenWidth}x{ack.ScreenHeight}");
        }

        foreach (var tile in packet.Tiles)
        {
            if (tile.GridX < 0 || tile.GridY < 0 || tile.Width <= 0 || tile.Height <= 0)
            {
                return (packet, $"tile {tile.GridX},{tile.GridY} has invalid geometry");
            }
            if ((tile.GridX + 1) * packet.TileSize > packet.FrameWidth + packet.TileSize ||
                (tile.GridY + 1) * packet.TileSize > packet.FrameHeight + packet.TileSize)
            {
                return (packet, $"tile {tile.GridX},{tile.GridY} lies outside the frame");
            }
            if (tile.Jpeg.Length < 4 || tile.Jpeg[0] != 0xFF || tile.Jpeg[1] != 0xD8 ||
                tile.Jpeg[^2] != 0xFF || tile.Jpeg[^1] != 0xD9)
            {
                return (packet, $"tile {tile.GridX},{tile.GridY} is not a JPEG (SOI/EOI markers missing)");
            }
        }

        return (packet, null);
    }

    private static string? ReadAgentErrors(string agentWorkDir)
    {
        var logPath = Path.Combine(agentWorkDir, "agent.log");
        if (!File.Exists(logPath))
        {
            return "agent.log was not created";
        }
        var errors = File.ReadAllLines(logPath).Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)).ToArray();
        return errors.Length == 0 ? null : string.Join(" | ", errors.Take(3));
    }

    private static async Task<(byte Type, byte[] Payload)?> ReceiveAsync(WebSocket ws, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return null;
        }
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await WsFraming.ReceiveAsync(ws, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    private static Process Start(string fileName, string workDir, StringBuilder log, string[] args, Dictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                psi.Environment[key] = value;
            }
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        void Capture(object _, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
            {
                lock (log)
                {
                    log.AppendLine(e.Data);
                }
            }
        }
        process.OutputDataReceived += Capture;
        process.ErrorDataReceived += Capture;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
            // process already gone
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RemoteControl.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static int Report(List<(bool Ok, string Name, string Detail)> results, StringBuilder relayLog,
        StringBuilder agentLog, string workDir, bool keep)
    {
        Console.WriteLine();
        Console.WriteLine("================ E2E RESULTS ================");
        foreach (var (ok, name, detail) in results)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name,-22} {detail}");
        }

        var failed = results.Count(r => !r.Ok);
        Console.WriteLine($"---------------------------------------------");
        Console.WriteLine(failed == 0 ? "ALL CHECKS PASSED" : $"{failed} CHECK(S) FAILED");

        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("---- relay output (tail) ----");
            Console.WriteLine(Tail(relayLog, 30));
            Console.WriteLine("---- agent output (tail) ----");
            Console.WriteLine(Tail(agentLog, 30));
            Console.WriteLine($"---- work dir: {workDir} ----");
        }

        if (!keep)
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* files may still be locked */ }
        }
        else
        {
            Console.WriteLine($"work dir kept at {workDir}");
        }

        return failed == 0 ? 0 : 1;
    }

    private static string Tail(StringBuilder log, int lines)
    {
        lock (log)
        {
            var all = log.ToString().Split('\n');
            return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
        }
    }

    private sealed record Options(
        int Port,
        string Token,
        string AgentId,
        string? RelayDir,
        string? AgentDir,
        bool Keep,
        bool TestInput,
        bool Tls,
        bool SkipControllerFirst,
        string? PfxPath,
        string? PfxPassword,
        string? Pin)
    {
        public static Options Parse(string[] args)
        {
            var port = 18099;
            var token = "e2e-token";
            var id = "e2e";
            string? relayDir = null;
            string? agentDir = null;
            var keep = false;
            var testInput = false;
            var tls = false;
            var skipControllerFirst = false;
            string? pfxPath = null;
            string? pfxPassword = null;
            string? pin = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                    case "--token" when i + 1 < args.Length: token = args[++i]; break;
                    case "--id" when i + 1 < args.Length: id = args[++i]; break;
                    case "--relay-dir" when i + 1 < args.Length: relayDir = args[++i]; break;
                    case "--agent-dir" when i + 1 < args.Length: agentDir = args[++i]; break;
                    case "--pfx" when i + 1 < args.Length: pfxPath = args[++i]; break;
                    case "--pfx-pass" when i + 1 < args.Length: pfxPassword = args[++i]; break;
                    case "--pin" when i + 1 < args.Length: pin = args[++i]; break;
                    case "--keep": keep = true; break;
                    case "--test-input": testInput = true; break;
                    case "--tls": tls = true; break;
                    case "--skip-cf": skipControllerFirst = true; break;
                }
            }

            return new Options(port, token, id, relayDir, agentDir, keep, testInput, tls, skipControllerFirst, pfxPath, pfxPassword, pin);
        }
    }
}
