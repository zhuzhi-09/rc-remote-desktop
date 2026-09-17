using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Rc.Protocol;

namespace Rc.Controller;

/// <summary>
/// Renders tile-based remote frames from an offscreen back buffer and turns mouse/keyboard
/// events into <see cref="InputMessage"/> objects. All buffer access happens under one lock,
/// so frames may be applied from the network thread.
/// </summary>
internal sealed class ScreenView : Control
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly object _lock = new();

    private Bitmap? _buffer;
    private Graphics? _bufferGraphics;
    private int _frameWidth;
    private int _frameHeight;
    private bool _hasFrame;
    private Rectangle _imageRect = Rectangle.Empty;
    private bool _fitToWindow = true;

    private int _rawVk = -1;
    private bool _rawExtended;
    private int _pressedButton = -1;
    private double _lastX;
    private double _lastY;

    public ScreenView()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Selectable | ControlStyles.ResizeRedraw | ControlStyles.ContainerControl,
            true);
        DoubleBuffered = true;
        TabStop = true;
        BackColor = Theme.Letterbox;
        Cursor = Cursors.Default;
    }

    /// <summary>Raised on the UI thread for every mouse/keyboard event that must reach the agent.</summary>
    public event Action<InputMessage>? RemoteInput;

    /// <summary>Raised (on the network thread) when the remote frame geometry changes.</summary>
    public event Action<int, int>? FrameSizeChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FitToWindow
    {
        get => _fitToWindow;
        set
        {
            if (_fitToWindow == value)
            {
                return;
            }

            _fitToWindow = value;
            Invalidate();
        }
    }

    public bool HasFrame
    {
        get
        {
            lock (_lock)
            {
                return _hasFrame;
            }
        }
    }

    public int FrameWidth
    {
        get
        {
            lock (_lock)
            {
                return _frameWidth;
            }
        }
    }

    public int FrameHeight
    {
        get
        {
            lock (_lock)
            {
                return _frameHeight;
            }
        }
    }

    /// <summary>Allocates the back buffer when the remote frame size changed.</summary>
    public void EnsureSize(int width, int height)
    {
        lock (_lock)
        {
            AllocateLocked(width, height);
        }
    }

    /// <summary>Blits every tile of the packet into the back buffer. Returns true when pixels changed.</summary>
    public bool ApplyFrame(FramePacket packet)
    {
        if (packet.FrameWidth <= 0 || packet.FrameHeight <= 0)
        {
            return false;
        }

        bool resized;
        bool hadFrame;
        bool changed;

        lock (_lock)
        {
            hadFrame = _hasFrame;
            resized = _buffer is null || _frameWidth != packet.FrameWidth || _frameHeight != packet.FrameHeight;
            if (resized)
            {
                AllocateLocked(packet.FrameWidth, packet.FrameHeight);
            }

            var graphics = _bufferGraphics;
            if (graphics is null)
            {
                return false;
            }

            changed = false;
            foreach (var tile in packet.Tiles)
            {
                if (tile.GridX < 0 || tile.GridY < 0 || tile.Width <= 0 || tile.Height <= 0 || tile.Jpeg.Length == 0)
                {
                    continue;
                }

                try
                {
                    using var stream = new MemoryStream(tile.Jpeg, writable: false);
                    using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                    var dest = new Rectangle(tile.GridX * packet.TileSize, tile.GridY * packet.TileSize, tile.Width, tile.Height);
                    graphics.DrawImage(image, dest, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                    changed = true;
                }
                catch (Exception)
                {
                    // A corrupt tile is skipped; the next keyframe repairs the region.
                }
            }

            _hasFrame = true;
        }

        if (resized && hadFrame)
        {
            FrameSizeChanged?.Invoke(packet.FrameWidth, packet.FrameHeight);
        }

        return changed || resized;
    }

    /// <summary>Inverts the letterbox mapping. Returns normalized 0..1 coordinates, or null outside the image.</summary>
    public PointF? ClientToRemote(Point point)
    {
        Rectangle rect;
        lock (_lock)
        {
            rect = _imageRect;
        }

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        if (point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom)
        {
            return null;
        }

        var normalizedX = (point.X - rect.Left) / (double)rect.Width;
        var normalizedY = (point.Y - rect.Top) / (double)rect.Height;
        return new PointF(
            (float)Math.Clamp(normalizedX, 0.0, 1.0),
            (float)Math.Clamp(normalizedY, 0.0, 1.0));
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(Theme.Letterbox);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var dest = Rectangle.Empty;
        Bitmap? buffer;

        lock (_lock)
        {
            buffer = _buffer;
            dest = ComputeDestination();
            _imageRect = dest;

            if (buffer is not null && dest.Width > 0 && dest.Height > 0)
            {
                g.InterpolationMode = dest.Width >= _frameWidth && dest.Height >= _frameHeight
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(buffer, dest, 0, 0, buffer.Width, buffer.Height, GraphicsUnit.Pixel);
            }
        }

        if (buffer is not null && dest.Width > 0 && dest.Height > 0)
        {
            g.PixelOffsetMode = PixelOffsetMode.Default;
            using var pen = new Pen(Theme.Border);
            g.DrawRectangle(pen, dest.X - 1, dest.Y - 1, dest.Width + 1, dest.Height + 1);
        }
    }

    private Rectangle ComputeDestination()
    {
        if (_buffer is null || _frameWidth <= 0 || _frameHeight <= 0)
        {
            return Rectangle.Empty;
        }

        var clientWidth = ClientSize.Width;
        var clientHeight = ClientSize.Height;
        if (clientWidth <= 0 || clientHeight <= 0)
        {
            return Rectangle.Empty;
        }

        int width;
        int height;
        var scale = Math.Min(clientWidth / (double)_frameWidth, clientHeight / (double)_frameHeight);

        if (!_fitToWindow)
        {
            width = _frameWidth;
            height = _frameHeight;
        }
        else if (scale >= 1.0)
        {
            var integer = Math.Max(1, (int)Math.Floor(scale));
            width = _frameWidth * integer;
            height = _frameHeight * integer;
        }
        else
        {
            width = Math.Max(1, (int)Math.Round(_frameWidth * scale));
            height = Math.Max(1, (int)Math.Round(_frameHeight * scale));
        }

        return new Rectangle((clientWidth - width) / 2, (clientHeight - height) / 2, width, height);
    }

    private void AllocateLocked(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_buffer is not null && _frameWidth == width && _frameHeight == height)
        {
            return;
        }

        _bufferGraphics?.Dispose();
        _buffer?.Dispose();

        _buffer = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        _bufferGraphics = Graphics.FromImage(_buffer);
        _bufferGraphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        _bufferGraphics.PixelOffsetMode = PixelOffsetMode.Half;
        _bufferGraphics.CompositingMode = CompositingMode.SourceCopy;
        _bufferGraphics.Clear(Theme.Letterbox);

        _frameWidth = width;
        _frameHeight = height;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _bufferGraphics?.Dispose();
                _buffer?.Dispose();
                _bufferGraphics = null;
                _buffer = null;
            }
        }

        base.Dispose(disposing);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        EmitMove(e.Location);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        var button = MapButton(e.Button);
        if (button < 0 || !TryGetNormalized(e.Location, out var x, out var y))
        {
            return;
        }

        _pressedButton = button;
        _lastX = x;
        _lastY = y;

        RemoteInput?.Invoke(new InputMessage { Kind = InputKind.MouseMove, X = x, Y = y });
        RemoteInput?.Invoke(new InputMessage { Kind = InputKind.MouseDown, X = x, Y = y, Button = button });
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        var button = MapButton(e.Button);
        if (button < 0)
        {
            return;
        }

        double x;
        double y;
        if (TryGetNormalized(e.Location, out var nx, out var ny))
        {
            x = nx;
            y = ny;
        }
        else if (_pressedButton == button)
        {
            // Released outside the image: release at the last known position so the agent never sticks.
            x = _lastX;
            y = _lastY;
        }
        else
        {
            return;
        }

        _pressedButton = -1;
        RemoteInput?.Invoke(new InputMessage { Kind = InputKind.MouseMove, X = x, Y = y });
        RemoteInput?.Invoke(new InputMessage { Kind = InputKind.MouseUp, X = x, Y = y, Button = button });
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        if (!TryGetNormalized(e.Location, out var x, out var y))
        {
            return;
        }

        RemoteInput?.Invoke(new InputMessage { Kind = InputKind.MouseMove, X = x, Y = y });
        RemoteInput?.Invoke(new InputMessage { Kind = InputKind.Wheel, X = x, Y = y, Delta = e.Delta });
    }

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Tab:
            case Keys.Up:
            case Keys.Down:
            case Keys.Left:
            case Keys.Right:
            case Keys.Enter:
            case Keys.Escape:
            case Keys.Space:
            case Keys.Insert:
            case Keys.Delete:
            case Keys.Home:
            case Keys.End:
            case Keys.PageUp:
            case Keys.PageDown:
            case Keys.F11:
                return true;
        }

        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.F11 || e.KeyCode == Keys.None)
        {
            return; // Fullscreen is handled by the form (KeyPreview).
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        SendKey(InputKind.KeyDown, e.KeyCode);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.KeyCode == Keys.F11 || e.KeyCode == Keys.None)
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        SendKey(InputKind.KeyUp, e.KeyCode);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmKeyDown:
            case WmKeyUp:
                CaptureRawKey(m);
                base.WndProc(ref m);
                return;

            case WmSysKeyDown:
                CaptureRawKey(m);
                if ((int)m.WParam.ToInt64() != (int)Keys.F11)
                {
                    OnKeyDown(new KeyEventArgs((Keys)(m.WParam.ToInt64() & 0xFFFF)));
                }

                return;

            case WmSysKeyUp:
                CaptureRawKey(m);
                OnKeyUp(new KeyEventArgs((Keys)(m.WParam.ToInt64() & 0xFFFF)));
                return;
        }

        base.WndProc(ref m);
    }

    private void CaptureRawKey(Message m)
    {
        _rawVk = (int)(m.WParam.ToInt64() & 0xFFFF);
        _rawExtended = (m.LParam.ToInt64() & 0x0100_0000L) != 0;
    }

    private void SendKey(string kind, Keys key)
    {
        var virtualKey = _rawVk >= 0 ? _rawVk : (int)key;
        var extended = _rawExtended || IsExtendedKey(virtualKey);
        _rawVk = -1;
        _rawExtended = false;

        if (virtualKey <= 0)
        {
            return;
        }

        RemoteInput?.Invoke(new InputMessage { Kind = kind, Vk = virtualKey, Extended = extended });
    }

    private static bool IsExtendedKey(int virtualKey) => virtualKey switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true, // page up/down, end, home, arrows
        0x2D or 0x2E or 0x5D => true, // insert, delete, context menu
        0xA3 or 0xA5 => true, // right ctrl, right alt
        _ => false, // numpad enter / numpad navigation carry the raw extended bit
    };

    private void EmitMove(Point location)
    {
        if (!TryGetNormalized(location, out var x, out var y))
        {
            return;
        }

        _lastX = x;
        _lastY = y;
        RemoteInput?.Invoke(new InputMessage { Kind = InputKind.MouseMove, X = x, Y = y });
    }

    private bool TryGetNormalized(Point location, out double x, out double y)
    {
        var normalized = ClientToRemote(location);
        if (normalized is null)
        {
            x = 0;
            y = 0;
            return false;
        }

        x = normalized.Value.X;
        y = normalized.Value.Y;
        return true;
    }

    private static int MapButton(MouseButtons button) => button switch
    {
        MouseButtons.Left => MouseButton.Left,
        MouseButtons.Right => MouseButton.Right,
        MouseButtons.Middle => MouseButton.Middle,
        _ => -1,
    };
}
