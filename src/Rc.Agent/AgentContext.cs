using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Rc.Agent;

/// <summary>
/// Tray-application context. Hosts the capture pump (background thread) and drives the connection.
/// The capture pump runs at <see cref="AgentConfig.TargetFps"/>, encodes only changed tiles, and
/// forces a full keyframe on connect and at the configured interval.
/// </summary>
public sealed class AgentContext : ApplicationContext
{
    private readonly AgentConfig _config;
    private readonly ScreenCapturer _capturer = new();
    private readonly TileDeltaEncoder _encoder;
    private readonly AgentConnection _connection;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _captureThread;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _openWindowItem = new("打开窗口");
    private readonly ToolStripMenuItem _sharingItem = new("暂停共享");
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _notifyIcon;

    private SynchronizationContext? _ui;
    private Bitmap? _scaled;
    private volatile int _scale;
    private volatile bool _sharing;
    private AgentWindow? _window;
    private int _forceKeyframe;
    private uint _frameId;
    private long _lastKeyframeTicks;
    private string _state = "Starting";
    private bool _disposed;

    public AgentContext(AgentConfig config)
    {
        _config = config;
        _scale = config.Scale;
        _sharing = !config.StartPaused;
        _ui = SynchronizationContext.Current;

        _encoder = new TileDeltaEncoder(config.TileSize, config.JpegQuality);
        _connection = new AgentConnection(config, () => (_capturer.ScreenWidth, _capturer.ScreenHeight));
        _connection.StateChanged += OnStateChanged;
        _connection.KeyframeRequested += () => Interlocked.Exchange(ref _forceKeyframe, 1);
        _connection.QualityRequested += quality =>
        {
            _encoder.Quality = quality;
            Interlocked.Exchange(ref _forceKeyframe, 1);
        };
        _connection.ScaleRequested += scale =>
        {
            _scale = Math.Clamp(scale, 10, 100);
            Interlocked.Exchange(ref _forceKeyframe, 1);
        };

        _statusItem = new ToolStripMenuItem("Status: starting") { Enabled = false };
        _menu = BuildMenu();
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Rc Agent",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenLog();

        if (_config.ShowWindow)
        {
            _window = CreateWindow();
            _window.SetSharing(_sharing);
            _window.Show();
        }

        // A created window installs the WinForms synchronization context; pick it up so background
        // state changes are marshalled onto the UI thread.
        _ui ??= SynchronizationContext.Current;
        UpdateSharingUi();

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "RcCapture",
        };

        _connection.Start();
        _captureThread.Start();
    }

    /// <summary>True while this machine is actively sharing its screen.</summary>
    public bool SharingEnabled => _sharing;

    private ContextMenuStrip BuildMenu()
    {
        _openWindowItem.Click += (_, _) => ShowMainWindow();
        _sharingItem.Click += (_, _) => SetSharing(!_sharing);

        var reconnect = new ToolStripMenuItem("Reconnect", null, (_, _) => _connection.Reconnect());
        var openLog = new ToolStripMenuItem("Open log", null, (_, _) => OpenLog());
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_openWindowItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_sharingItem);
        menu.Items.Add(reconnect);
        menu.Items.Add(openLog);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    /// <summary>
    /// Turns screen sharing on or off. Disabling stops all screen access in the capture loop;
    /// enabling forces a fresh keyframe so the controller can rebuild its picture.
    /// </summary>
    private void SetSharing(bool enabled)
    {
        _sharing = enabled;
        if (enabled)
        {
            Interlocked.Exchange(ref _forceKeyframe, 1);
        }

        UpdateSharingUi();
    }

    private void UpdateSharingUi()
    {
        try
        {
            _sharingItem.Text = _sharing ? "暂停共享" : "恢复共享";
            _notifyIcon.Text = ComposeTrayText();
            _window?.SetSharing(_sharing);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to update the sharing UI: {ex.Message}");
        }
    }

    private string ComposeTrayText()
    {
        var text = _sharing ? $"Rc Agent - {_state}" : $"Rc Agent - {_state} / 已暂停";
        return text.Length > 63 ? text[..63] : text;
    }

    private AgentWindow CreateWindow()
    {
        var window = new AgentWindow(_sharing);
        window.SharingToggled += SetSharing;
        window.OpenLogRequested += OpenLog;
        window.ExitRequested += ExitThread;
        window.SetStatus(_state);
        return window;
    }

    /// <summary>Shows the status window, creating it on first use when it started hidden.</summary>
    private void ShowMainWindow()
    {
        if (_window is null)
        {
            _window = CreateWindow();
            _ui ??= SynchronizationContext.Current;
        }

        _window.SetSharing(_sharing);
        if (!_window.Visible)
        {
            _window.Show();
        }

        if (_window.WindowState == FormWindowState.Minimized)
        {
            _window.WindowState = FormWindowState.Normal;
        }

        _window.Activate();
    }

    private void CaptureLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            try
            {
                // While paused the local user has stopped sharing: no screen access, no encoding,
                // no frames. Control falls through to the frame-budget sleep below.
                if (_sharing)
                {
                    var captured = _capturer.Capture();
                    var frame = _scale < 100 ? ScaleFrame(captured) : captured;

                    var frameId = unchecked(++_frameId);
                    var now = Environment.TickCount64;
                    var force = Interlocked.Exchange(ref _forceKeyframe, 0) == 1
                        || now - _lastKeyframeTicks >= _config.KeyframeIntervalSeconds * 1000L;

                    var packet = _encoder.Encode(frame, force, frameId);
                    if (packet.Keyframe)
                    {
                        _lastKeyframeTicks = now;
                    }

                    if (packet.Keyframe || packet.Tiles.Count > 0)
                    {
                        _connection.PostFrame(packet);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Capture loop error.", ex);
            }

            var budget = 1000 / Math.Max(1, _config.TargetFps);
            var elapsed = (int)(Environment.TickCount64 - started);
            var sleep = budget - elapsed;
            if (sleep > 0)
            {
                _stop.Token.WaitHandle.WaitOne(sleep);
            }
        }
    }

    private Bitmap ScaleFrame(Bitmap source)
    {
        var percent = Math.Clamp(_scale, 10, 100);
        var width = Math.Max(1, (int)Math.Round(source.Width * (percent / 100.0)));
        var height = Math.Max(1, (int)Math.Round(source.Height * (percent / 100.0)));

        if (_scaled is null || _scaled.Width != width || _scaled.Height != height)
        {
            _scaled?.Dispose();
            _scaled = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        }

        using var graphics = Graphics.FromImage(_scaled);
        graphics.InterpolationMode = InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return _scaled;
    }

    private void OnStateChanged(string state)
    {
        _state = state;
        _window?.SetStatus(state);

        if (_ui is not null)
        {
            _ui.Post(_ => ApplyTrayText(), null);
        }
        else
        {
            ApplyTrayText();
        }
    }

    private void ApplyTrayText()
    {
        try
        {
            _notifyIcon.Text = ComposeTrayText();
            _statusItem.Text = "Status: " + _state;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to update the tray text: {ex.Message}");
        }
    }

    private void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Log.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to open the log file.", ex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _stop.Cancel();
            try
            {
                _captureThread.Join(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Log.Warn($"Capture thread did not stop cleanly: {ex.Message}");
            }

            _window?.Dispose();
            _window = null;
            _connection.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _encoder.Dispose();
            _capturer.Dispose();
            _scaled?.Dispose();
            _stop.Dispose();
        }

        base.Dispose(disposing);
    }
}
