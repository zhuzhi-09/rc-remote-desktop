using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace Rc.Controller;

/// <summary>Design tokens for the controller UI: one dark palette, one type scale, one radius.</summary>
internal static class Theme
{
    public static readonly Color Back = Color.FromArgb(15, 18, 23);
    public static readonly Color Surface = Color.FromArgb(22, 27, 34);
    public static readonly Color SurfaceAlt = Color.FromArgb(30, 36, 45);
    public static readonly Color Hover = Color.FromArgb(41, 49, 60);
    public static readonly Color Border = Color.FromArgb(45, 53, 66);
    public static readonly Color Text = Color.FromArgb(230, 234, 240);
    public static readonly Color TextMuted = Color.FromArgb(137, 147, 163);
    public static readonly Color Accent = Color.FromArgb(59, 130, 246);
    public static readonly Color Danger = Color.FromArgb(229, 90, 90);
    public static readonly Color Success = Color.FromArgb(61, 214, 140);
    public static readonly Color Warning = Color.FromArgb(242, 176, 61);
    public static readonly Color Letterbox = Color.FromArgb(6, 8, 11);
    public static readonly Color PillBack = Color.FromArgb(19, 24, 31);

    public const int Radius = 6;

    public static readonly Font UiFont = new("Segoe UI", 9F);
    public static readonly Font GlyphFont = new("Segoe UI", 6.5F);

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.A + (b.A - a.A) * t),
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    /// <summary>Toolbar caption label.</summary>
    public static Label CreateToolLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = TextMuted,
        BackColor = Color.Transparent,
        Font = UiFont,
        Margin = new Padding(4, 9, 0, 0),
    };

    /// <summary>Applies the fixed-width status bar section look (tint per severity at runtime).</summary>
    public static void StyleStatusLabel(Label label, string text, int width)
    {
        label.Text = text;
        label.AutoSize = false;
        label.Size = new Size(width, 16);
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.ForeColor = Text;
        label.BackColor = Color.Transparent;
        label.Font = UiFont;
        label.Margin = new Padding(0, 0, 20, 0);
    }

    /// <summary>Applies base/hover/pressed colours to an owner-drawn button.</summary>
    public static void ApplyPalette(FlatButton button, Color baseColor, Color textColor)
    {
        button.BaseColor = baseColor;
        button.HoverColor = Mix(baseColor, Color.White, 0.12f);
        button.PressColor = Mix(baseColor, Color.Black, 0.18f);
        button.TextColor = textColor;
        button.Invalidate();
    }

    /// <summary>Applies the dark owner-drawn look (including dropdown items) to the scale selector.</summary>
    public static void StyleScaleCombo(ComboBox combo)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = SurfaceAlt;
        combo.ForeColor = Text;
        combo.Font = UiFont;
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.ItemHeight = 18;
        combo.Size = new Size(76, 24);
        combo.Margin = new Padding(6, 3, 14, 0);
        combo.Items.AddRange(["100%", "75%", "50%"]);
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0)
            {
                return;
            }

            e.DrawBackground();
            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var brush = new SolidBrush(selected ? Hover : SurfaceAlt))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }

            var text = combo.Items[e.Index]?.ToString() ?? "";
            TextRenderer.DrawText(
                e.Graphics,
                text,
                UiFont,
                e.Bounds,
                Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
    }

    /// <summary>Human readable transfer rate.</summary>
    public static string FormatRate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / (1024 * 1024):0.00} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0.0} KB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };

    /// <summary>Draws a 1px divider along the top or bottom edge of a chrome panel.</summary>
    public static void AddDivider(Panel panel, bool top)
    {
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            var y = top ? 0 : panel.Height - 1;
            e.Graphics.DrawLine(pen, 0, y, panel.Width, y);
        };
    }
}

/// <summary>Fully owner-drawn button: rounded, hover/pressed/focus states, no system chrome.</summary>
internal sealed class FlatButton : Button
{
    private bool _hovered;
    private bool _pressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BaseColor { get; set; } = Theme.SurfaceAlt;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverColor { get; set; } = Theme.Hover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PressColor { get; set; } = Theme.Border;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TextColor { get; set; } = Theme.Text;

    public FlatButton(string text)
    {
        Text = text;
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        DoubleBuffered = true;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Theme.Surface;
        Font = Theme.UiFont;
        Cursor = Cursors.Hand;
        TabStop = true;
        Size = new Size(88, 30);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var fill = !Enabled
            ? Theme.Mix(BaseColor, Theme.Surface, 0.55f)
            : _pressed ? PressColor : _hovered ? HoverColor : BaseColor;

        using (var path = Theme.RoundedPath(bounds, Theme.Radius))
        {
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            var borderColor = _hovered && Enabled ? Theme.Mix(Theme.Border, Theme.Text, 0.18f) : Theme.Mix(Theme.Border, fill, 0.35f);
            using (var pen = new Pen(borderColor))
            {
                g.DrawPath(pen, path);
            }

            if (Focused && Enabled)
            {
                var focusBounds = new Rectangle(2, 2, Math.Max(0, Width - 5), Math.Max(0, Height - 5));
                using var focusPath = Theme.RoundedPath(focusBounds, Math.Max(2, Theme.Radius - 2));
                using var focusPen = new Pen(Theme.Mix(Theme.Accent, Color.White, 0.3f));
                g.DrawPath(focusPen, focusPath);
            }
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            bounds,
            Enabled ? TextColor : Theme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }
}

/// <summary>Single-line text input with a 1px accent border drawn by its container.</summary>
internal sealed class FieldBox : Panel
{
    public FieldBox(int width, bool password = false)
    {
        BackColor = Theme.Border;
        Padding = new Padding(1);
        Size = new Size(width, 30);

        Inner = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            Font = Theme.UiFont,
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = password,
        };
        Inner.GotFocus += (_, _) => BackColor = Theme.Accent;
        Inner.LostFocus += (_, _) => BackColor = Theme.Border;
        Controls.Add(Inner);
    }

    public TextBox Inner { get; }

    [AllowNull]
    public override string Text
    {
        get => Inner.Text;
        set => Inner.Text = value;
    }
}

/// <summary>Clamped integer spinner (text box plus ▲/▼) used for the JPEG quality setting.</summary>
internal sealed class NumericField : Panel
{
    private readonly TextBox _box;
    private int _value;

    public NumericField(int width, int min, int max, int value)
    {
        Min = min;
        Max = max;
        _value = Math.Clamp(value, min, max);
        BackColor = Theme.Border;
        Size = new Size(width, 30);

        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            Font = Theme.UiFont,
            TextAlign = HorizontalAlignment.Center,
            Text = _value.ToString(CultureInfo.InvariantCulture),
            Bounds = new Rectangle(1, 1, width - 23, 28),
        };
        _box.KeyPress += OnBoxKeyPress;
        _box.KeyDown += OnBoxKeyDown;
        _box.GotFocus += (_, _) => BackColor = Theme.Accent;
        _box.LostFocus += (_, _) => Commit();

        var up = new FlatButton("\u25B2")
        {
            BaseColor = Theme.SurfaceAlt,
            HoverColor = Theme.Hover,
            PressColor = Theme.Border,
            TextColor = Theme.TextMuted,
            Font = Theme.GlyphFont,
            Bounds = new Rectangle(width - 21, 1, 20, 13),
            TabStop = false,
        };
        var down = new FlatButton("\u25BC")
        {
            BaseColor = Theme.SurfaceAlt,
            HoverColor = Theme.Hover,
            PressColor = Theme.Border,
            TextColor = Theme.TextMuted,
            Font = Theme.GlyphFont,
            Bounds = new Rectangle(width - 21, 15, 20, 13),
            TabStop = false,
        };
        up.Click += (_, _) => SetValue(_value + 1);
        down.Click += (_, _) => SetValue(_value - 1);

        Controls.Add(_box);
        Controls.Add(up);
        Controls.Add(down);
    }

    public int Min { get; }

    public int Max { get; }

    public event EventHandler? ValueChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, Min, Max);
            _box.Text = _value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void OnBoxKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
        {
            e.Handled = true;
        }
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        Commit();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void Commit()
    {
        BackColor = Theme.Border;
        if (int.TryParse(_box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            SetValue(parsed);
        }

        _box.Text = _value.ToString(CultureInfo.InvariantCulture);
    }

    private void SetValue(int value)
    {
        var clamped = Math.Clamp(value, Min, Max);
        if (clamped == _value)
        {
            _box.Text = clamped.ToString(CultureInfo.InvariantCulture);
            return;
        }

        _value = clamped;
        _box.Text = clamped.ToString(CultureInfo.InvariantCulture);
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Centered keyboard-capture hint drawn as a dark rounded badge.</summary>
internal sealed class HintPill : Control
{
    public HintPill(string text)
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        DoubleBuffered = true;
        BackColor = Theme.PillBack;
        ForeColor = Theme.TextMuted;
        Font = Theme.UiFont;
        Text = text;
        Cursor = Cursors.Hand;
        TabStop = false;

        var measured = TextRenderer.MeasureText(text, Font);
        Size = new Size(measured.Width + 34, measured.Height + 18);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedPath(bounds, Math.Max(4, Height / 2)))
        {
            using (var brush = new SolidBrush(Theme.Surface))
            {
                g.FillPath(brush, path);
            }

            using (var pen = new Pen(Theme.Border))
            {
                g.DrawPath(pen, path);
            }
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            bounds,
            ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
