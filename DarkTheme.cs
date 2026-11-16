using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gyro2stick;

/// <summary>
/// Material Design 3 dark theme for the WinForms window: Material colour roles, rounded cards for the sections,
/// pill buttons, underlined tabs, Material checkboxes and sliders, and the tray menu.
/// </summary>
static class DarkTheme
{
    // Material 3 baseline dark colour roles.
    public static readonly Color Background = Color.FromArgb(0x14, 0x12, 0x18);   // surface
    public static readonly Color Canvas = Color.FromArgb(0x0F, 0x0D, 0x13);       // surface container lowest
    public static readonly Color Card = Color.FromArgb(0x21, 0x1F, 0x26);         // surface container
    public static readonly Color Surface = Color.FromArgb(0x2B, 0x29, 0x30);      // surface container high
    public static readonly Color Field = Color.FromArgb(0x36, 0x34, 0x3B);        // surface container highest
    public static readonly Color Border = Color.FromArgb(0x49, 0x45, 0x4F);       // outline variant
    public static readonly Color Outline = Color.FromArgb(0x93, 0x8F, 0x99);
    public static readonly Color Text = Color.FromArgb(0xE6, 0xE0, 0xE9);         // on surface
    public static readonly Color SubtleText = Color.FromArgb(0xCA, 0xC4, 0xD0);   // on surface variant
    public static readonly Color Highlight = Field;
    public static readonly Color Primary = Color.FromArgb(0xD0, 0xBC, 0xFF);
    public static readonly Color OnPrimary = Color.FromArgb(0x38, 0x1E, 0x72);
    public static readonly Color PrimaryContainer = Color.FromArgb(0x4F, 0x37, 0x8B);
    public static readonly Color OnPrimaryContainer = Color.FromArgb(0xEA, 0xDD, 0xFF);
    public static readonly Color Tonal = Color.FromArgb(0x4A, 0x44, 0x58);        // secondary container
    public static readonly Color OnTonal = Color.FromArgb(0xE8, 0xDE, 0xF8);

    public const string KeepColors = "theme:keep";
    public const string Subtle = "theme:subtle";

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Dark title bar (Windows 10 2004+ / 11), tinted with the window colour on Windows 11.</summary>
    public static void ApplyTitleBar(Form form)
    {
        int on = 1;
        if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0) // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));     // pre-20H1 value
        int caption = ColorTranslator.ToWin32(Background);
        DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));    // DWMWA_CAPTION_COLOR (Windows 11)
    }

    /// <summary>Dark scrollbars on a scrolling control (now, or when its handle is created).</summary>
    public static void DarkScrollbars(Control c)
    {
        if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
    }

    static void SetTheme(Control c, string theme)
    {
        if (c.IsHandleCreated) SetWindowTheme(c.Handle, theme, null);
        c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, theme, null);
    }

    /// <summary>List item with a rounded Material selection (secondary container) instead of system blue.</summary>
    static void DrawListItem(ListBox lb, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(lb.BackColor)) g.FillRectangle(bg, e.Bounds);
        if (e.Index < 0) return;
        float k = K(lb);
        bool selected = (e.State & DrawItemState.Selected) != 0;
        if (selected)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(e.Bounds.X + 3 * k, e.Bounds.Y + 1 * k, e.Bounds.Width - 6 * k, e.Bounds.Height - 2 * k);
            using var path = Rounded(r, r.Height / 2);
            using var fill = new SolidBrush(Tonal);
            g.FillPath(fill, path);
        }
        var textRect = Rectangle.FromLTRB(e.Bounds.X + (int)(14 * k), e.Bounds.Y, e.Bounds.Right - (int)(8 * k), e.Bounds.Bottom);
        TextRenderer.DrawText(g, lb.GetItemText(lb.Items[e.Index]), lb.Font, textRect, selected ? OnTonal : Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // ---------- per-control extras ----------

    static readonly ConditionalWeakTable<Control, object> Themed = new();
    static readonly ConditionalWeakTable<Button, Action<Graphics, Rectangle, Color>> Icons = new();
    static readonly ConditionalWeakTable<Control, StrongBox<bool>> Tabs = new();
    static readonly ConditionalWeakTable<Control, StrongBox<float>> Radii = new();

    /// <summary>Corner radius (96-dpi pixels) for a button that should not be a pill, e.g. a keyboard key.</summary>
    public static void SetRadius(Control b, float radius) => Radii.AddOrUpdate(b, new StrongBox<float>(radius));

    /// <summary>Draws an icon instead of text on a button (shown as a round Material icon button).</summary>
    public static void SetIcon(Button b, Action<Graphics, Rectangle, Color> draw)
    {
        Icons.AddOrUpdate(b, draw);
        b.Invalidate();
    }

    /// <summary>Shows a button as a Material tab; the selected one is in the primary colour with an underline.</summary>
    public static void SetTab(Button b, bool selected)
    {
        Tabs.AddOrUpdate(b, new StrongBox<bool>(selected));
        b.Invalidate();
    }

    static float K(Control c) => c.DeviceDpi / 96f;

    static bool InCard(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p is GroupBox) return true;
        return false;
    }

    /// <summary>The colour actually behind a control (its first parent with a solid colour).</summary>
    static Color BehindOf(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent)
        {
            if (p is GroupBox) return Card;
            if (p.BackColor.A == 255) return p.BackColor;
        }
        return Background;
    }

    /// <summary>Recolors a control tree. Controls that set their own colors (ON / Apply buttons) are tagged to skip.</summary>
    public static void Apply(Control root)
    {
        foreach (Control c in root.Controls) Apply(c);
        bool first = !Themed.TryGetValue(root, out _);
        if (first) Themed.Add(root, true);

        // Shapes are drawn for every control, colours only for the ones not tagged to keep their own.
        if (first) AttachPainter(root);
        if (root.Tag as string == KeepColors) return;

        Color around = InCard(root) ? Card : Background;
        switch (root)
        {
            case GroupBox g:
                g.BackColor = Background;
                g.ForeColor = Text;
                if (first)
                {
                    g.Padding = new Padding(g.Padding.Left + 2, g.Padding.Top + 5, g.Padding.Right + 2, g.Padding.Bottom + 1);
                    g.Margin = new Padding(2);
                }
                break;
            case ListView lv:
                lv.BackColor = Field;
                lv.ForeColor = Text;
                lv.BorderStyle = BorderStyle.None;
                if (first)
                {
                    lv.OwnerDraw = true;
                    lv.DrawColumnHeader += DrawHeader;
                    lv.DrawItem += (_, e) => e.DrawDefault = true;
                    lv.DrawSubItem += (_, e) => e.DrawDefault = true;
                    DarkScrollbars(lv);
                }
                break;
            case ComboBox cb:
                // Windows' own dark combo box (rounded, dark drop-down list) instead of the white-edged flat one.
                cb.FlatStyle = FlatStyle.Standard;
                cb.BackColor = Field;
                cb.ForeColor = Text;
                if (first) SetTheme(cb, "DarkMode_CFD");
                break;
            case NumericUpDown nud:
                nud.BackColor = Field;
                nud.ForeColor = Text;
                nud.BorderStyle = BorderStyle.None;
                if (first) foreach (Control part in nud.Controls) SetTheme(part, "DarkMode_Explorer");
                break;
            case TextBoxBase tb:
                tb.BackColor = Field;
                tb.ForeColor = Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ListBox lb:
                lb.BackColor = Field;
                lb.ForeColor = Text;
                lb.BorderStyle = BorderStyle.None;
                if (first && lb.DrawMode == DrawMode.Normal)
                {
                    lb.DrawMode = DrawMode.OwnerDrawVariable;
                    lb.MeasureItem += (_, e) => e.ItemHeight = lb.Font.Height + (int)(10 * K(lb));
                    lb.DrawItem += (_, e) => DrawListItem(lb, e);
                    DarkScrollbars(lb);
                }
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.BackColor = Tonal;
                b.ForeColor = OnTonal;
                break;
            case CheckBox cb:
                cb.BackColor = around;
                cb.ForeColor = Text;
                break;
            case Label l:
                l.BackColor = Color.Transparent;
                l.ForeColor = l.Tag as string == Subtle ? SubtleText : Text;
                break;
            case MacroFlowView or GyroScope:
                root.BackColor = Canvas;
                root.ForeColor = Text;
                break;
            default:
                root.BackColor = around;
                root.ForeColor = Text;
                break;
        }
    }

    static void AttachPainter(Control c)
    {
        switch (c)
        {
            case GroupBox g:
                g.Paint += (_, e) => PaintCard(g, e.Graphics);
                break;
            case Button or CheckBox { Appearance: Appearance.Button }:
                var bb = (ButtonBase)c;
                bb.FlatStyle = FlatStyle.Flat;
                bb.FlatAppearance.BorderSize = 0;
                bb.Paint += (_, e) => PaintButton(bb, e.Graphics);
                HookHover(bb);
                // Auto-sized buttons get room for the pill's rounded ends (scaled with the rest later).
                if (bb.AutoSize) bb.Padding = new Padding(bb.Padding.Left + 10, bb.Padding.Top, bb.Padding.Right + 10, bb.Padding.Bottom);
                break;
            case CheckBox cb:
                // The Material box and its gap are wider than the native glyph: make room for the text.
                if (cb.AutoSize) cb.Padding = new Padding(cb.Padding.Left, cb.Padding.Top, cb.Padding.Right + 2, cb.Padding.Bottom);
                cb.Paint += (_, e) => PaintCheck(cb, e.Graphics);
                HookHover(cb);
                break;
        }
    }

    static void HookHover(Control c)
    {
        c.MouseEnter += (_, _) => c.Invalidate();
        c.MouseLeave += (_, _) => c.Invalidate();
        c.MouseDown += (_, _) => c.Invalidate();
        c.MouseUp += (_, _) => c.Invalidate();
        c.EnabledChanged += (_, _) => c.Invalidate();
    }

    static bool Hovered(Control c) => c.Enabled && c.ClientRectangle.Contains(c.PointToClient(Cursor.Position));

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Color Blend(Color over, int alpha, Color under) => Color.FromArgb(
        (over.R * alpha + under.R * (255 - alpha)) / 255,
        (over.G * alpha + under.G * (255 - alpha)) / 255,
        (over.B * alpha + under.B * (255 - alpha)) / 255);

    static TextFormatFlags Align(ContentAlignment a)
    {
        var f = TextFormatFlags.NoPrefix;
        f |= a switch
        {
            ContentAlignment.TopLeft or ContentAlignment.MiddleLeft or ContentAlignment.BottomLeft => TextFormatFlags.Left,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter,
        };
        return f;
    }

    /// <summary>Draws text vertically centred (multi-line too), within the rectangle.</summary>
    static void DrawCentered(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags)
    {
        var size = TextRenderer.MeasureText(g, text, font, new Size(r.Width, int.MaxValue), flags);
        var box = new Rectangle(r.X, r.Y + Math.Max(0, (r.Height - size.Height) / 2), r.Width, Math.Min(r.Height, size.Height + 2));
        TextRenderer.DrawText(g, text, font, box, color, flags);
    }

    static void PaintCard(GroupBox g, Graphics gr)
    {
        float k = K(g);
        gr.Clear(BehindOf(g));
        gr.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Rounded(new RectangleF(0, 0, g.Width - 1, g.Height - 1), 10 * k))
        using (var fill = new SolidBrush(Card))
            gr.FillPath(fill, path);
        if (!string.IsNullOrEmpty(g.Text))
        {
            using var font = new Font(g.Font.FontFamily, g.Font.Size, FontStyle.Bold);
            var r = new Rectangle((int)(10 * k), (int)(4 * k), g.Width - (int)(20 * k), font.Height + 2);
            TextRenderer.DrawText(gr, g.Text, font, r, Primary,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    static void PaintButton(ButtonBase b, Graphics g)
    {
        float k = K(b);
        var behind = BehindOf(b);
        g.Clear(behind);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, b.Width - 1.5f, b.Height - 1.5f);
        bool hover = Hovered(b), down = hover && (Control.MouseButtons & MouseButtons.Left) != 0;
        int state = down ? 31 : hover ? 20 : 0; // Material state layers: 8 % hover, 12 % pressed

        // Tabs: text only, primary colour and an underline when selected.
        if (b is Button tb && Tabs.TryGetValue(tb, out var sel))
        {
            if (state > 0)
                using (var path = Rounded(rect, 10 * k))
                using (var layer = new SolidBrush(Color.FromArgb(state, Text)))
                    g.FillPath(layer, path);
            var color = sel.Value ? Primary : SubtleText;
            using var font = new Font(b.Font, sel.Value ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(g, b.Text, font, Rectangle.Round(rect), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (sel.Value)
            {
                int w = TextRenderer.MeasureText(g, b.Text, font).Width + (int)(8 * k);
                float h = 3 * k;
                var bar = new RectangleF((b.Width - w) / 2f, b.Height - h, w, h + 3 * k);
                using var path = Rounded(bar, h);
                using var brush = new SolidBrush(Primary);
                g.SetClip(new RectangleF(0, b.Height - h, b.Width, h));
                g.FillPath(brush, path);
                g.ResetClip();
            }
            return;
        }

        // Icon buttons: round, no fill until hovered.
        if (b is Button ib && Icons.TryGetValue(ib, out var icon))
        {
            float d = Math.Min(rect.Width, rect.Height);
            var circle = new RectangleF(rect.X + (rect.Width - d) / 2, rect.Y + (rect.Height - d) / 2, d, d);
            if (state > 0)
                using (var layer = new SolidBrush(Color.FromArgb(state, Text)))
                    g.FillEllipse(layer, circle);
            icon(g, b.ClientRectangle, b.Enabled ? SubtleText : Color.FromArgb(97, Text));
            return;
        }

        Color fill, text;
        if (!b.Enabled)
        {
            fill = Blend(Text, 31, behind);       // on-surface 12 %
            text = Blend(Text, 97, behind);       // on-surface 38 %
        }
        else
        {
            fill = b.BackColor.A == 0 ? behind : b.BackColor;
            text = b.ForeColor;
            if (state > 0) fill = Blend(text, state, fill);
        }
        float radius = Radii.TryGetValue(b, out var fixedRadius) ? fixedRadius.Value * k : Math.Min(rect.Height / 2, 20 * k);
        using (var path = Rounded(rect, radius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        int pad = (int)(Math.Min(radius, 12 * k));
        int textWidth = TextRenderer.MeasureText(g, b.Text, b.Font).Width;
        pad = Math.Max(1, Math.Min(pad, (int)(rect.Width - textWidth) / 2 - 1)); // never cut the text for the corners
        var textRect = Rectangle.Round(new RectangleF(rect.X + pad, rect.Y + 2 * k, rect.Width - 2 * pad, rect.Height - 4 * k));
        DrawCentered(g, b.Text, b.Font, textRect, text, Align(b.TextAlign));
    }

    static void PaintCheck(CheckBox cb, Graphics g)
    {
        float k = K(cb);
        var behind = BehindOf(cb);
        g.Clear(cb.BackColor.A == 255 ? cb.BackColor : behind);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float box = 13.5f * k; // same footprint as the native glyph, so layouts sized for it still fit
        float y = (cb.Height - box) / 2f;
        var r = new RectangleF(0.5f * k, y, box, box);
        bool hover = Hovered(cb);
        bool on = cb.CheckState != CheckState.Unchecked;
        Color accent = cb.Enabled ? Primary : Color.FromArgb(97, Text);

        if (hover)
        {
            var halo = RectangleF.Inflate(r, 3 * k, 3 * k);
            using var layer = new SolidBrush(Color.FromArgb(28, on ? Primary : Text));
            g.FillEllipse(layer, halo);
        }
        using (var path = Rounded(r, 3 * k))
        {
            if (on)
            {
                using var fill = new SolidBrush(accent);
                g.FillPath(fill, path);
                using var mark = new Pen(cb.Enabled ? OnPrimary : behind, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                if (cb.CheckState == CheckState.Indeterminate)
                    g.DrawLine(mark, r.X + box * 0.25f, r.Y + box / 2, r.Right - box * 0.25f, r.Y + box / 2);
                else
                    g.DrawLines(mark, new[]
                    {
                        new PointF(r.X + box * 0.22f, r.Y + box * 0.52f),
                        new PointF(r.X + box * 0.42f, r.Y + box * 0.72f),
                        new PointF(r.X + box * 0.78f, r.Y + box * 0.30f),
                    });
            }
            else
            {
                using var pen = new Pen(cb.Enabled ? (hover ? Text : SubtleText) : Color.FromArgb(97, Text), 2f * k);
                using var inner = Rounded(RectangleF.Inflate(r, -1 * k, -1 * k), 2 * k);
                g.DrawPath(pen, inner);
            }
        }
        var textRect = new Rectangle((int)(r.Right + 3.5f * k), 0, cb.Width - (int)(r.Right + 3.5f * k), cb.Height);
        DrawCentered(g, cb.Text, cb.Font, textRect, cb.Enabled ? cb.ForeColor : Color.FromArgb(97, Text),
            TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }

    static void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bg = new SolidBrush(Surface);
        e.Graphics.FillRectangle(bg, e.Bounds);
        using var pen = new Pen(Border);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var r = e.Bounds;
        r.Inflate(-8, 0);
        using var font = new Font(e.Font ?? Control.DefaultFont, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text, font, r, SubtleText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    public static void Apply(ToolStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = true };
        menu.BackColor = Surface;
        menu.ForeColor = Text;
        foreach (ToolStripItem item in menu.Items) item.ForeColor = Text;
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Field;
        public override Color MenuItemSelected => Field;
        public override Color MenuItemSelectedGradientBegin => Field;
        public override Color MenuItemSelectedGradientEnd => Field;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => PrimaryContainer;
        public override Color CheckSelectedBackground => PrimaryContainer;
        public override Color CheckPressedBackground => PrimaryContainer;
    }
}

/// <summary>Material 3 slider: a primary active track, a tonal inactive track and a round handle.
/// Same members as the TrackBar it replaces.</summary>
sealed class MaterialSlider : Control
{
    int _min, _max = 10, _value;
    bool _dragging;

    public event EventHandler? ValueChanged;
    public int SmallChange { get; set; } = 1;
    public int LargeChange { get; set; } = 5;

    public MaterialSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    public int Minimum
    {
        get => _min;
        set { _min = value; if (_max < _min) _max = _min; Value = _value; Invalidate(); }
    }

    public int Maximum
    {
        get => _max;
        set { _max = value; if (_min > _max) _min = _max; Value = _value; Invalidate(); }
    }

    public int Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, _min, _max);
            if (value == _value) return;
            _value = value;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    float K => DeviceDpi / 96f;
    float TrackLeft => 11 * K;
    float TrackRight => Width - 11 * K;

    float XOf(int v) => _max == _min ? TrackLeft : TrackLeft + (TrackRight - TrackLeft) * (v - _min) / (_max - _min);

    int ValueAt(int x)
    {
        float f = Math.Clamp((x - TrackLeft) / Math.Max(1, TrackRight - TrackLeft), 0, 1);
        return _min + (int)Math.Round(f * (_max - _min));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor.A == 255 ? BackColor : DarkTheme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = K, cy = Height / 2f, x = XOf(_value), h = 4 * k;
        Color active = Enabled ? DarkTheme.Primary : Color.FromArgb(97, DarkTheme.Text);
        Color inactive = Enabled ? DarkTheme.Tonal : Color.FromArgb(31, DarkTheme.Text);

        using (var path = DarkTheme.Rounded(new RectangleF(TrackLeft, cy - h / 2, TrackRight - TrackLeft, h), h / 2))
        using (var brush = new SolidBrush(inactive))
            g.FillPath(brush, path);
        if (x > TrackLeft)
            using (var path = DarkTheme.Rounded(new RectangleF(TrackLeft, cy - h / 2, x - TrackLeft, h), h / 2))
            using (var brush = new SolidBrush(active))
                g.FillPath(brush, path);

        bool hover = Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position));
        if (Enabled && (hover || _dragging || Focused))
        {
            float hr = Math.Min(13 * k, Height / 2f - 1);
            using var halo = new SolidBrush(Color.FromArgb(_dragging ? 50 : 32, DarkTheme.Primary));
            g.FillEllipse(halo, x - hr, cy - hr, 2 * hr, 2 * hr);
        }
        float r = 8 * k;
        using (var thumb = new SolidBrush(active))
            g.FillEllipse(thumb, x - r, cy - r, 2 * r, 2 * r);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _dragging = true;
        Capture = true;
        Value = ValueAt(e.X);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Value = ValueAt(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: Value -= SmallChange; break;
            case Keys.Right or Keys.Up: Value += SmallChange; break;
            case Keys.PageDown: Value -= LargeChange; break;
            case Keys.PageUp: Value += LargeChange; break;
            case Keys.Home: Value = _min; break;
            case Keys.End: Value = _max; break;
            default: return;
        }
        e.Handled = true;
    }
}
