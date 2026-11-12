using System.Drawing.Drawing2D;
using System.Reflection;

namespace DeviceWatch.Probe;

internal enum ButtonTone
{
    Primary,
    Secondary,
    Danger,
}

internal class ProbeForm : Form
{
    protected ProbeForm()
    {
        Font = ProbeTheme.Ui;
        BackColor = ProbeTheme.Canvas;
        ForeColor = ProbeTheme.Ink;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
    }
}

internal static class ProbeTheme
{
    public static readonly Color Canvas = Color.FromArgb(244, 246, 250);
    public static readonly Color Surface = Color.White;
    public static readonly Color Ink = Color.FromArgb(23, 32, 42);
    public static readonly Color Muted = Color.FromArgb(100, 112, 128);
    public static readonly Color Line = Color.FromArgb(226, 230, 237);
    public static readonly Color Soft = Color.FromArgb(241, 245, 249);
    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);
    public static readonly Color DangerHover = Color.FromArgb(185, 28, 28);
    public static readonly Color DangerSoft = Color.FromArgb(254, 226, 226);
    public static readonly Color Selection = Color.FromArgb(219, 234, 254);

    public static readonly Font Ui = new("Segoe UI", 10F);
    public static readonly Font Title = new("Segoe UI", 20F, FontStyle.Bold);
    public static readonly Font Section = new("Segoe UI", 11F, FontStyle.Bold);
    public static readonly Font Caption = new("Segoe UI", 9F);
    public static readonly Font Button = new("Segoe UI", 9.5F, FontStyle.Bold);
    public static readonly Font Mono = new("Cascadia Mono", 9.5F);

    public static Panel Page()
        => new()
        {
            Dock = DockStyle.Fill,
            BackColor = Canvas,
            Padding = new Padding(20, 16, 20, 16),
        };

    public static Panel Card(int height = 0)
    {
        var card = new Panel
        {
            Dock = height > 0 ? DockStyle.Top : DockStyle.Fill,
            BackColor = Line,
            Padding = new Padding(1),
            Margin = new Padding(0),
        };
        if (height > 0)
        {
            card.Height = height;
        }

        return card;
    }

    public static Panel CardBody()
        => new()
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(14, 10, 14, 12),
        };

    public static Label Heading(string text)
        => new()
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 28,
            Font = Section,
            ForeColor = Ink,
            BackColor = Surface,
            TextAlign = ContentAlignment.MiddleLeft,
        };

    public static Label CaptionLabel(string text, int height)
        => new()
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = height,
            Font = Caption,
            ForeColor = Muted,
            BackColor = Surface,
        };

    public static void StyleField(TextBox box, bool muted = false)
    {
        box.BorderStyle = BorderStyle.None;
        box.BackColor = muted ? Soft : Surface;
        box.ForeColor = Ink;
        box.Font = muted ? Mono : Ui;
    }

    public static void StyleList(ListView view)
    {
        view.BorderStyle = BorderStyle.None;
        view.BackColor = Surface;
        view.ForeColor = Ink;
        view.Font = Ui;
        view.FullRowSelect = true;
        view.GridLines = false;
        view.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        view.OwnerDraw = true;
        view.DrawItem += (_, e) =>
        {
            if (view.View != View.Details)
            {
                e.DrawDefault = true;
            }
        };
        view.DrawColumnHeader += DrawHeader;
        view.DrawSubItem += DrawCell;
        EnableDoubleBuffer(view);
    }

    public static void EnableDoubleBuffer(Control control)
    {
        typeof(Control).InvokeMember(
            "DoubleBuffered",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.SetProperty,
            null,
            control,
            [true]);
    }

    private static void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var back = new SolidBrush(Soft);
        e.Graphics.FillRectangle(back, e.Bounds);
        var textBounds = new Rectangle(e.Bounds.X + 10, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 14), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Caption, textBounds, Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        using var pen = new Pen(Line);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    private static void DrawCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        var item = e.Item;
        if (item is null)
        {
            return;
        }

        var tinted = item.BackColor.ToArgb() != SystemColors.Window.ToArgb()
            && item.BackColor.ToArgb() != Surface.ToArgb()
            && !item.BackColor.IsEmpty;
        var back = tinted ? item.BackColor : item.Selected ? Selection : Surface;
        using var brush = new SolidBrush(back);
        e.Graphics.FillRectangle(brush, e.Bounds);
        if (item.Selected && e.ColumnIndex == 0)
        {
            using var bar = new SolidBrush(Accent);
            e.Graphics.FillRectangle(bar, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
        }

        var textBounds = new Rectangle(e.Bounds.X + (e.ColumnIndex == 0 ? 12 : 10), e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", Ui, textBounds, Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        using var pen = new Pen(Color.FromArgb(241, 245, 249));
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }
}

internal sealed class ThemeButton : Button
{
    private bool _hot;
    private bool _pressed;

    public ThemeButton()
    {
        Tone = ButtonTone.Secondary;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = ProbeTheme.Canvas;
        FlatAppearance.MouseDownBackColor = ProbeTheme.Canvas;
        UseVisualStyleBackColor = false;
        BackColor = ProbeTheme.Canvas;
        Font = ProbeTheme.Button;
        AutoSize = false;
        Size = new Size(112, 36);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public ButtonTone Tone { get; set; }

    protected override bool ShowFocusCues => false;

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(Parent?.BackColor ?? ProbeTheme.Canvas);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hot = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hot = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && Enabled)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var canvas = Parent?.BackColor ?? ProbeTheme.Canvas;
        var g = e.Graphics;
        g.Clear(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        var (fill, text, border) = Palette();
        var outer = new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f);
        using var outerPath = Rounded(outer, 7f);
        if (border.A > 0)
        {
            using var borderBrush = new SolidBrush(border);
            g.FillPath(borderBrush, outerPath);
            var inner = new RectangleF(outer.X + 1f, outer.Y + 1f, outer.Width - 2f, outer.Height - 2f);
            using var innerPath = Rounded(inner, 6f);
            using var fillBrush = new SolidBrush(fill);
            g.FillPath(fillBrush, innerPath);
        }
        else
        {
            using var fillBrush = new SolidBrush(fill);
            g.FillPath(fillBrush, outerPath);
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            new Rectangle(0, 0, Width, Height),
            text,
            fill,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private (Color Fill, Color Text, Color Border) Palette()
    {
        if (!Enabled)
        {
            return (ProbeTheme.Soft, Color.FromArgb(148, 163, 184), ProbeTheme.Line);
        }

        return Tone switch
        {
            ButtonTone.Primary => (_pressed ? Color.FromArgb(30, 64, 175) : _hot ? ProbeTheme.AccentHover : ProbeTheme.Accent, Color.White, Color.Transparent),
            ButtonTone.Danger => (_pressed ? Color.FromArgb(153, 27, 27) : _hot ? ProbeTheme.DangerHover : ProbeTheme.Danger, Color.White, Color.Transparent),
            _ => (_pressed ? Color.FromArgb(226, 232, 240) : _hot ? ProbeTheme.Soft : ProbeTheme.Surface, ProbeTheme.Ink, ProbeTheme.Line),
        };
    }

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class StatusPill : Control
{
    public StatusPill()
    {
        Size = new Size(96, 30);
        Font = ProbeTheme.Button;
        BackColor = ProbeTheme.Soft;
        ForeColor = ProbeTheme.Muted;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(Parent?.BackColor ?? ProbeTheme.Canvas);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? ProbeTheme.Canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Rounded(bounds, bounds.Height / 2f);
        using var brush = new SolidBrush(BackColor);
        g.FillPath(brush, path);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), ForeColor, BackColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2f, bounds.Height);
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
