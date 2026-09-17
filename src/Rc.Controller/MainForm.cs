using Rc.Protocol;

namespace Rc.Controller;

/// <summary>Main controller window: dark toolbar, remote screen surface and status bar.</summary>
internal sealed class MainForm : Form
{
    private const int ToolbarHeight = 52;
    private const int StatusHeight = 28;

    private readonly ControllerConfig _config;
    private readonly RelayConnection _relay = new();
    private readonly ScreenView _view = new();
    private readonly HintPill _hint;
    private readonly Panel _toolbar = new();
    private readonly FlowLayoutPanel _toolbarItems = new();
    private readonly Panel _content = new();
    private readonly Panel _statusBar = new();
    private readonly FlowLayoutPanel _statusItems = new();

    private readonly FlatButton _connectButton = new("连接");
    private readonly FlatButton _keyframeButton = new("重发关键帧");
    private readonly FlatButton _fullscreenButton = new("全屏 (F11)");
    private readonly FieldBox _agentBox = new(150);
    private readonly FieldBox _tokenBox = new(130, password: true);
    private readonly NumericField _qualityField = new(66, 10, 95, 60);
    private readonly ComboBox _scaleCombo = new();
    private readonly CheckBox _fitCheck = new();
    private readonly Label _connectionStatus = new();
    private readonly Label _resolutionStatus = new();
    private readonly Label _fpsStatus = new();
    private readonly Label _rateStatus = new();

    private bool _fullscreen;
    private FormBorderStyle _savedBorderStyle;
    private FormWindowState _savedWindowState;
    private bool _savedTopMost;
    private int _reportedWidth;
    private int _reportedHeight;

    public MainForm(ControllerConfig config)
    {
        _config = config;
        _hint = new HintPill("点击画面以捕获键盘");

        Text = "Rc 主控端";
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = Theme.UiFont;
        ClientSize = new Size(1220, 800);
        MinimumSize = new Size(1140, 640);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        BuildToolbar();
        BuildStatusBar();
        BuildContent();
        WireEvents();
        BindConfig();

        FormClosing += (_, _) =>
        {
            _relay.Stop();
            _config.Save();
        };
        Shown += (_, _) => CenterHint();
    }

    private void BuildToolbar()
    {
        _toolbar.BackColor = Theme.Surface;
        Theme.AddDivider(_toolbar, top: false);

        _toolbarItems.Dock = DockStyle.Fill;
        _toolbarItems.WrapContents = false;
        _toolbarItems.AutoSize = false;
        _toolbarItems.BackColor = Theme.Surface;
        _toolbarItems.Padding = new Padding(12, 11, 12, 11);
        _toolbar.Controls.Add(_toolbarItems);

        _toolbarItems.Controls.Add(Theme.CreateToolLabel("被控端 ID"));
        _agentBox.Margin = new Padding(6, 0, 14, 0);
        _toolbarItems.Controls.Add(_agentBox);

        _toolbarItems.Controls.Add(Theme.CreateToolLabel("令牌"));
        _tokenBox.Margin = new Padding(6, 0, 14, 0);
        _toolbarItems.Controls.Add(_tokenBox);

        _connectButton.Size = new Size(84, 30);
        _connectButton.Margin = new Padding(2, 0, 6, 0);
        Theme.ApplyPalette(_connectButton, Theme.Accent, Color.White);
        _toolbarItems.Controls.Add(_connectButton);

        _keyframeButton.Size = new Size(104, 30);
        _keyframeButton.Margin = new Padding(0, 0, 6, 0);
        Theme.ApplyPalette(_keyframeButton, Theme.SurfaceAlt, Theme.Text);
        _toolbarItems.Controls.Add(_keyframeButton);

        _toolbarItems.Controls.Add(Theme.CreateToolLabel("画质"));
        _qualityField.Margin = new Padding(6, 0, 14, 0);
        _toolbarItems.Controls.Add(_qualityField);

        _toolbarItems.Controls.Add(Theme.CreateToolLabel("缩放"));
        Theme.StyleScaleCombo(_scaleCombo);
        _toolbarItems.Controls.Add(_scaleCombo);

        _fitCheck.Text = "适应窗口";
        _fitCheck.FlatStyle = FlatStyle.Flat;
        _fitCheck.ForeColor = Theme.Text;
        _fitCheck.BackColor = Color.Transparent;
        _fitCheck.AutoSize = true;
        _fitCheck.Checked = true;
        _fitCheck.Margin = new Padding(4, 9, 14, 0);
        _toolbarItems.Controls.Add(_fitCheck);

        _fullscreenButton.Size = new Size(112, 30);
        _fullscreenButton.Margin = new Padding(4, 0, 0, 0);
        Theme.ApplyPalette(_fullscreenButton, Theme.SurfaceAlt, Theme.Text);
        _toolbarItems.Controls.Add(_fullscreenButton);
    }

    private void BuildStatusBar()
    {
        _statusBar.BackColor = Theme.Surface;
        Theme.AddDivider(_statusBar, top: true);

        _statusItems.Dock = DockStyle.Fill;
        _statusItems.WrapContents = false;
        _statusItems.AutoSize = false;
        _statusItems.BackColor = Theme.Surface;
        _statusItems.Padding = new Padding(14, 6, 14, 6);
        _statusBar.Controls.Add(_statusItems);

        Theme.StyleStatusLabel(_connectionStatus, "连接状态：未连接", 310);
        _statusItems.Controls.Add(_connectionStatus);
        Theme.StyleStatusLabel(_resolutionStatus, "分辨率：—", 200);
        _statusItems.Controls.Add(_resolutionStatus);
        Theme.StyleStatusLabel(_fpsStatus, "FPS：0.0", 120);
        _statusItems.Controls.Add(_fpsStatus);
        Theme.StyleStatusLabel(_rateStatus, "速率：0 B/s", 160);
        _statusItems.Controls.Add(_rateStatus);
    }

    private void BuildContent()
    {
        _content.BackColor = Theme.Back;
        _content.Padding = new Padding(8);
        _view.Dock = DockStyle.Fill;
        _content.Controls.Add(_view);

        _hint.Parent = _content;
        _hint.MouseDown += (_, _) => _view.Focus();
        _view.Resize += (_, _) => CenterHint();

        Controls.Add(_content);
        Controls.Add(_statusBar);
        Controls.Add(_toolbar);
    }

    private void WireEvents()
    {
        _connectButton.Click += (_, _) => ToggleConnection();
        _keyframeButton.Click += (_, _) => SendControl(ControlKind.RequestKeyframe, 0);
        _fullscreenButton.Click += (_, _) => ToggleFullscreen();
        _fitCheck.CheckedChanged += (_, _) => _view.FitToWindow = _fitCheck.Checked;

        _qualityField.ValueChanged += (_, _) =>
        {
            _config.JpegQuality = _qualityField.Value;
            _config.Save();
            SendControl(ControlKind.SetQuality, _qualityField.Value);
        };

        _scaleCombo.SelectedIndexChanged += (_, _) =>
        {
            var scale = SelectedScale();
            _config.Scale = scale;
            _config.Save();
            SendControl(ControlKind.SetScale, scale);
        };

        _relay.Connected += () => Ui(() => SetConnectionState(true, false));
        _relay.Disconnected += reason => Ui(() =>
        {
            SetConnectionState(false, _relay.IsRunning);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                SetConnectionLabel("已断开（" + reason + "）", Theme.TextMuted);
            }
        });
        _relay.HelloAckReceived += ack => Ui(() => OnHelloAck(ack));
        _relay.FrameReceived += OnFrame;
        _relay.ErrorOccurred += message => Ui(() => SetConnectionLabel("错误：" + message, Theme.Danger));
        _relay.Log += message => Ui(() => SetConnectionLabel(message, Theme.Warning));
        _relay.StatsUpdated += (fps, bytesPerSecond) => Ui(() => UpdateStats(fps, bytesPerSecond));

        _view.RemoteInput += message => _relay.SendInput(message);
        _view.FrameSizeChanged += (_, _) => SendControl(ControlKind.RequestKeyframe, 0);
        _view.GotFocus += (_, _) => _hint.Visible = false;
        _view.LostFocus += (_, _) =>
        {
            _hint.Visible = true;
            CenterHint();
        };
    }

    private void BindConfig()
    {
        _agentBox.Text = _config.AgentId;
        _tokenBox.Text = _config.Token;
        _qualityField.Value = _config.JpegQuality;
        _scaleCombo.SelectedIndex = _config.Scale switch { 50 => 2, 75 => 1, _ => 0 };
        _fitCheck.Checked = true;
        _view.FitToWindow = true;
        SetConnectionState(false, false);
    }

    private void ToggleConnection()
    {
        if (_relay.IsRunning)
        {
            _relay.Stop();
            SetConnectionState(false, false);
            SetConnectionLabel("已断开", Theme.TextMuted);
            return;
        }

        _config.AgentId = _agentBox.Text.Trim();
        _config.Token = _tokenBox.Text.Trim();
        _config.Save();

        _reportedWidth = 0;
        _reportedHeight = 0;
        _relay.Start(_config);
        SetConnectionState(false, true);
        SetConnectionLabel("连接中…", Theme.Warning);
    }

    private void OnHelloAck(HelloAckMessage ack)
    {
        if (ack.ScreenWidth > 0 && ack.ScreenHeight > 0)
        {
            _reportedWidth = ack.ScreenWidth;
            _reportedHeight = ack.ScreenHeight;
            _resolutionStatus.Text = $"分辨率：{ack.ScreenWidth}×{ack.ScreenHeight}";
            _view.EnsureSize(ack.ScreenWidth, ack.ScreenHeight);
        }

        if (ack.Ok && !ack.PeerOnline)
        {
            SetConnectionLabel("已连接（等待被控端上线）", Theme.Warning);
        }
        else if (ack.Ok)
        {
            SetConnectionLabel("已连接", Theme.Success);
            SendControl(ControlKind.SetQuality, _config.JpegQuality);
            SendControl(ControlKind.SetScale, _config.Scale);
        }
    }

    private void OnFrame(FramePacket frame)
    {
        if (IsDisposed)
        {
            return;
        }

        var changed = _view.ApplyFrame(frame);

        if (frame.FrameWidth != _reportedWidth || frame.FrameHeight != _reportedHeight)
        {
            _reportedWidth = frame.FrameWidth;
            _reportedHeight = frame.FrameHeight;
            var width = frame.FrameWidth;
            var height = frame.FrameHeight;
            Ui(() => _resolutionStatus.Text = $"分辨率：{width}×{height}");
        }

        if (changed)
        {
            Ui(() =>
            {
                if (!_view.IsDisposed)
                {
                    _view.Invalidate();
                }
            });
        }
    }

    private void SetConnectionState(bool connected, bool busy)
    {
        _connectButton.Text = connected || busy ? "断开" : "连接";
        Theme.ApplyPalette(_connectButton, connected || busy ? Theme.Danger : Theme.Accent, Color.White);

        _keyframeButton.Enabled = connected;
        _qualityField.Enabled = connected;
        _scaleCombo.Enabled = connected;
        _agentBox.Inner.Enabled = !(connected || busy);
        _tokenBox.Inner.Enabled = !(connected || busy);

        if (connected)
        {
            SetConnectionLabel("已连接", Theme.Success);
        }
        else if (!busy)
        {
            SetConnectionLabel("未连接", Theme.TextMuted);
        }

        if (!connected)
        {
            _fpsStatus.Text = "FPS：0.0";
            _rateStatus.Text = "速率：0 B/s";
        }
    }

    private void SetConnectionLabel(string text, Color color)
    {
        _connectionStatus.Text = "连接状态：" + text;
        _connectionStatus.ForeColor = color;
    }

    private void UpdateStats(double fps, double bytesPerSecond)
    {
        _fpsStatus.Text = $"FPS：{fps:0.0}";
        _rateStatus.Text = "速率：" + Theme.FormatRate(bytesPerSecond);
    }

    private int SelectedScale() => _scaleCombo.SelectedIndex switch
    {
        1 => 75,
        2 => 50,
        _ => 100,
    };

    private void SendControl(string kind, int value) =>
        _relay.SendControl(new ControlMessage { Kind = kind, Value = value });

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _savedBorderStyle = FormBorderStyle;
            _savedWindowState = WindowState;
            _savedTopMost = TopMost;

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            _statusBar.Visible = false;
            _fullscreenButton.Text = "退出全屏 (F11)";
            _fullscreen = true;
        }
        else
        {
            WindowState = FormWindowState.Normal;
            FormBorderStyle = _savedBorderStyle;
            WindowState = _savedWindowState;
            TopMost = _savedTopMost;
            _statusBar.Visible = true;
            _fullscreenButton.Text = "全屏 (F11)";
            _fullscreen = false;
        }

        PerformLayout();
        CenterHint();
    }

    private void CenterHint()
    {
        _hint.Location = new Point(
            _view.Left + ((_view.Width - _hint.Width) / 2),
            _view.Top + ((_view.Height - _hint.Height) / 2));
        _hint.BringToFront();
    }

    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SafeRun(action)));
            }
            else
            {
                SafeRun(action);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void SafeRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Program.LogException(ex, "界面");
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);

        var client = ClientRectangle;
        var statusHeight = _statusBar.Visible ? StatusHeight : 0;

        _toolbar.Bounds = new Rectangle(0, 0, client.Width, ToolbarHeight);
        _statusBar.Bounds = new Rectangle(0, client.Height - statusHeight, client.Width, StatusHeight);
        _content.Bounds = new Rectangle(
            0,
            ToolbarHeight,
            client.Width,
            Math.Max(0, client.Height - ToolbarHeight - statusHeight));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _relay.Dispose();
            _view.Dispose();
        }

        base.Dispose(disposing);
    }
}
