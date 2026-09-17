using System.Drawing;
using System.Windows.Forms;

namespace Rc.Agent;

/// <summary>
/// The local status window for the person sitting at the controlled PC. It is deliberately visible
/// and simple: it shows the connection and sharing state and gives the user a one-click way to
/// pause or resume screen sharing. Closing it with the X button only hides it; the agent keeps
/// running in the tray.
/// </summary>
public sealed class AgentWindow : Form
{
    private const string WindowTitle = "Rc 远程协助";
    private const int BalloonDurationMs = 4000;

    private readonly Label _statusLabel;
    private readonly Label _sharingLabel;
    private readonly Label _hintLabel;
    private readonly Button _toggleButton;
    private readonly CheckBox _autostartCheck;
    private readonly Font _toggleFont;

    private bool _sharing;
    private bool _autostartReverting;
    private string _state = "Starting";
    private bool _trayHintShown;
    private NotifyIcon? _trayHint;
    private System.Windows.Forms.Timer? _trayHintTimer;

    public AgentWindow(bool sharingEnabled)
    {
        Text = WindowTitle;
        Icon = SystemIcons.Application;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        ClientSize = new Size(414, 292);

        _toggleFont = new Font(Font.FontFamily, 12F, FontStyle.Bold);

        _statusLabel = new Label
        {
            AutoSize = true,
            Text = "连接状态：正在启动",
            Margin = new Padding(0, 0, 0, 8),
        };

        _sharingLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0),
        };

        _toggleButton = new Button
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 14),
            Font = _toggleFont,
            UseVisualStyleBackColor = true,
        };
        _toggleButton.Click += (_, _) => SharingToggled?.Invoke(!_sharing);

        _hintLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Text = "暂停后本机画面将不再被采集，也不会发送给远端。",
            Margin = new Padding(0),
        };

        // Set before subscribing, so initializing from the registry never raises CheckedChanged.
        _autostartCheck = new CheckBox
        {
            AutoSize = true,
            Text = "开机自动启动",
            Margin = new Padding(0, 4, 0, 2),
            Checked = Autostart.IsEnabled(),
        };
        _autostartCheck.CheckedChanged += OnAutostartChanged;

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
        };

        var exitButton = new Button
        {
            Text = "退出程序",
            Size = new Size(96, 30),
            Margin = new Padding(8, 0, 0, 0),
            UseVisualStyleBackColor = true,
        };
        exitButton.Click += (_, _) => ExitRequested?.Invoke();

        var openLogButton = new Button
        {
            Text = "打开日志",
            Size = new Size(96, 30),
            Margin = new Padding(0),
            UseVisualStyleBackColor = true,
        };
        openLogButton.Click += (_, _) => OpenLogRequested?.Invoke();

        bottom.Controls.Add(exitButton);
        bottom.Controls.Add(openLogButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 12),
            ColumnCount = 1,
            RowCount = 6,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));

        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(_sharingLabel, 0, 1);
        layout.Controls.Add(_toggleButton, 0, 2);
        layout.Controls.Add(_autostartCheck, 0, 3);
        layout.Controls.Add(_hintLabel, 0, 4);
        layout.Controls.Add(bottom, 0, 5);
        Controls.Add(layout);

        SetSharing(sharingEnabled);
    }

    /// <summary>Raised when the user clicks the toggle; the argument is the desired sharing state.</summary>
    public event Action<bool>? SharingToggled;

    /// <summary>Raised when the user clicks "退出程序".</summary>
    public event Action? ExitRequested;

    /// <summary>Raised when the user clicks "打开日志".</summary>
    public event Action? OpenLogRequested;

    /// <summary>Updates the button text and sharing label without raising <see cref="SharingToggled"/>.</summary>
    public void SetSharing(bool enabled)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<bool>(SetSharing), enabled);
            return;
        }

        _sharing = enabled;
        _toggleButton.Text = enabled ? "暂停共享" : "恢复共享";
        _sharingLabel.Text = enabled ? "共享状态：正在发送画面" : "共享状态：已暂停，未发送任何画面";
        _sharingLabel.ForeColor = enabled ? SystemColors.ControlText : Color.Firebrick;
    }

    /// <summary>Updates the connection-state label.</summary>
    public void SetStatus(string state)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(SetStatus), state);
            return;
        }

        _state = state;
        _statusLabel.ForeColor = SystemColors.ControlText;
        _statusLabel.Text = "连接状态：" + LocalizeState(state);
    }

    /// <summary>
    /// Applies the auto-start checkbox. The registry write is fast; on failure the checkbox is
    /// reverted with the change notification suppressed and the reason is shown in the status label.
    /// </summary>
    private void OnAutostartChanged(object? sender, EventArgs e)
    {
        if (_autostartReverting)
        {
            return;
        }

        var desired = _autostartCheck.Checked;
        try
        {
            if (Autostart.TrySet(desired, out var error))
            {
                SetStatus(_state);
                return;
            }

            RevertAutostartCheck(!desired);
            ShowAutostartError(error);
        }
        catch (Exception ex)
        {
            // Registry/IO failures are reported through the TrySet error path, but never let an
            // unexpected one escape the UI thread.
            RevertAutostartCheck(!desired);
            ShowAutostartError("无法修改开机自动启动设置：" + ex.Message);
            Log.Warn($"Failed to change the auto-start setting: {ex.Message}");
        }
    }

    private void RevertAutostartCheck(bool value)
    {
        _autostartReverting = true;
        try
        {
            _autostartCheck.Checked = value;
        }
        finally
        {
            _autostartReverting = false;
        }
    }

    private void ShowAutostartError(string message)
    {
        _statusLabel.Text = message;
        _statusLabel.ForeColor = Color.Firebrick;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // The X button only hides the window: the agent must keep running until Exit is chosen.
            e.Cancel = true;
            Hide();
            ShowTrayHintOnce();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DismissTrayHint();
            _toggleFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string LocalizeState(string state) => state switch
    {
        "Starting" => "正在启动",
        "Connecting" => "正在连接",
        "Connected" => "已连接",
        "Reconnecting" => "正在重连",
        "Stopped" => "已停止",
        _ => state,
    };

    private void ShowTrayHintOnce()
    {
        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;

        try
        {
            _trayHint = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = WindowTitle,
                Visible = true,
            };
            _trayHint.ShowBalloonTip(BalloonDurationMs, WindowTitle, "已最小化到托盘，程序仍在运行。", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to show the tray notification: {ex.Message}");
        }

        // Balloons are suppressed in some Windows configurations, so always remove the temporary
        // tray icon after a short grace period.
        _trayHintTimer = new System.Windows.Forms.Timer { Interval = BalloonDurationMs + 2000 };
        _trayHintTimer.Tick += (_, _) => DismissTrayHint();
        _trayHintTimer.Start();
    }

    private void DismissTrayHint()
    {
        if (_trayHintTimer is not null)
        {
            _trayHintTimer.Stop();
            _trayHintTimer.Dispose();
            _trayHintTimer = null;
        }

        if (_trayHint is not null)
        {
            _trayHint.Visible = false;
            _trayHint.Dispose();
            _trayHint = null;
        }
    }
}
