using System.Drawing.Drawing2D;

namespace DSGyro360;

/// <summary>
/// Flow-chart view of one macro: the trigger at the top, one block per step joined by arrows, and an end block
/// that loops back to the first step when the macro repeats. Click a block to select it, drag a step to move it.
/// </summary>
sealed class MacroFlowView : Panel
{
    Macro? _macro;
    int _selected = -1;
    int _dragIndex = -1, _dropIndex = -1;
    Point _dragStart;
    bool _dragging;
    int _dragY;

    public event Action? SelectionChanged;
    public event Action? StepsReordered;

    /// <summary>Text that follows the scroll offset (TextRenderer ignores the Graphics translation by default).</summary>
    static void DrawText(Graphics g, string? text, Font font, Rectangle r, Color color, TextFormatFlags flags = TextFormatFlags.Default) =>
        TextRenderer.DrawText(g, text, font, r, color, flags | TextFormatFlags.PreserveGraphicsTranslateTransform);

    public MacroFlowView()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        ResizeRedraw = true;
        BackColor = DarkTheme.Canvas;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    public Macro? Macro
    {
        get => _macro;
        set
        {
            _macro = value;
            _selected = -1;
            UpdateScrollSize();
            Invalidate();
        }
    }

    /// <summary>-1 = the trigger block, 0..n-1 = a step, n = the end block.</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            int n = _macro?.Steps.Count ?? 0;
            _selected = Math.Clamp(value, -1, n);
            EnsureVisible(_selected);
            Invalidate();
        }
    }

    float K => DeviceDpi / 96f;
    int NodeH => (int)(54 * K);
    int Gap => (int)(30 * K);
    int Top0 => (int)(18 * K);
    int LoopRoom => (int)(56 * K);

    int NodeW => Math.Max((int)(220 * K), Math.Min((int)(360 * K), ClientSize.Width - LoopRoom - (int)(30 * K)));

    /// <summary>Bounds of a block, in scrolled content coordinates. Row 0 is the trigger.</summary>
    Rectangle NodeRect(int index)
    {
        int row = index + 1;
        int x = Math.Max((int)(12 * K), (ClientSize.Width - LoopRoom - NodeW) / 2);
        return new Rectangle(x, Top0 + row * (NodeH + Gap), NodeW, NodeH);
    }

    public void UpdateScrollSize()
    {
        int n = _macro?.Steps.Count ?? 0;
        AutoScrollMinSize = new Size(0, NodeRect(Math.Max(n, 1)).Bottom + Top0);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollSize();
    }

    void EnsureVisible(int index)
    {
        if (_macro == null) return;
        var r = NodeRect(index);
        int top = -AutoScrollPosition.Y;
        if (r.Top < top) AutoScrollPosition = new Point(0, r.Top - Gap);
        else if (r.Bottom > top + ClientSize.Height) AutoScrollPosition = new Point(0, r.Bottom - ClientSize.Height + Gap);
    }

    static Color KindColor(MacroStepKind k) => k switch
    {
        MacroStepKind.Button or MacroStepKind.ButtonDown or MacroStepKind.ButtonUp => Color.FromArgb(0x2E, 0x7D, 0x5B),
        MacroStepKind.Key or MacroStepKind.KeyDown or MacroStepKind.KeyUp => Color.FromArgb(0x6A, 0x4F, 0xB0),
        MacroStepKind.Mouse => Color.FromArgb(0xB3, 0x5F, 0x16),
        MacroStepKind.Wait => Color.FromArgb(0x5E, 0x5A, 0x66),
        MacroStepKind.Stick => Color.FromArgb(0x00, 0x6F, 0x7A),
        _ => Color.FromArgb(0xA8, 0x36, 0x4B),
    };

    static string KindBadge(MacroStepKind k) => k switch
    {
        MacroStepKind.Button or MacroStepKind.ButtonDown or MacroStepKind.ButtonUp => "PAD",
        MacroStepKind.Key or MacroStepKind.KeyDown or MacroStepKind.KeyUp => "KEY",
        MacroStepKind.Mouse => "MOUSE",
        MacroStepKind.Wait => "WAIT",
        MacroStepKind.Stick => "STICK",
        _ => "RUMBLE",
    };

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float k = K;

        if (_macro == null)
        {
            DrawText(g, "No macro yet.\nClick  + New macro…  and pick an example to start.", Font, ClientRectangle, DarkTheme.SubtleText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        var m = _macro;
        int n = m.Steps.Count;

        using var title = new Font(Font.FontFamily, Font.Size * 1.05f, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, Font.Size * 0.9f);
        using var badgeFont = new Font(Font.FontFamily, Font.Size * 0.72f, FontStyle.Bold);
        using var arrowPen = new Pen(DarkTheme.Outline, 2f * k) { CustomEndCap = new AdjustableArrowCap(4, 5) };

        // Arrows down the chain.
        for (int i = -1; i < n; i++)
        {
            var a = NodeRect(i);
            var b = NodeRect(i + 1);
            g.DrawLine(arrowPen, a.X + a.Width / 2f, a.Bottom + 2 * k, b.X + b.Width / 2f, b.Top - 3 * k);
        }

        // Loop back from the end to the first step when the macro repeats.
        bool repeats = m.Repeat != MacroRepeat.Once || m.Iterations > 1;
        if (repeats && n > 0)
        {
            var end = NodeRect(n);
            var first = NodeRect(0);
            float x = end.Right + LoopRoom * 0.55f;
            using var loopPen = new Pen(DarkTheme.Primary, 2.2f * k) { CustomEndCap = new AdjustableArrowCap(4, 5) };
            var pts = new[]
            {
                new PointF(end.Right + 2 * k, end.Top + end.Height / 2f),
                new PointF(x, end.Top + end.Height / 2f),
                new PointF(x, first.Top + first.Height / 2f),
                new PointF(first.Right + 4 * k, first.Top + first.Height / 2f),
            };
            g.DrawLines(loopPen, pts);
            string label = m.Repeat switch
            {
                MacroRepeat.WhileHeld => "while held",
                MacroRepeat.Toggle => "until pressed",
                _ => $"×{m.Iterations}",
            };
            if (m.PauseBetweenMs > 0) label += $"\n+{m.PauseBetweenMs} ms";
            var mid = new RectangleF(x - LoopRoom * 0.5f, (first.Bottom + end.Top) / 2f - 20 * k, LoopRoom, 40 * k);
            using var lb = new SolidBrush(BackColor);
            var size = g.MeasureString(label, small);
            g.FillRectangle(lb, x - size.Width / 2, mid.Y + mid.Height / 2 - size.Height / 2, size.Width, size.Height);
            using var lt = new SolidBrush(DarkTheme.Primary);
            g.DrawString(label, small, lt, x - size.Width / 2, mid.Y + mid.Height / 2 - size.Height / 2);
        }

        // Trigger block (pill)
        {
            var r = NodeRect(-1);
            using var path = Rounded(r, r.Height / 2f);
            using var fill = new SolidBrush(DarkTheme.PrimaryContainer);
            g.FillPath(fill, path);
            DrawSelection(g, path, -1);
            var text = new Rectangle(r.X + (int)(18 * k), r.Y + (int)(6 * k), r.Width - (int)(36 * k), r.Height / 2);
            DrawText(g, $"When  {m.ComboText}  is {m.TriggerText}", title, text, DarkTheme.OnPrimaryContainer,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            var sub = new Rectangle(text.X, r.Y + r.Height / 2, text.Width, r.Height / 2 - (int)(6 * k));
            string info = (m.Enabled ? "" : "DISABLED · ") + (m.BlockSource ? "game doesn't see the combo" : "game also sees the combo");
            DrawText(g, info, small, sub, DarkTheme.Primary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // Steps
        for (int i = 0; i < n; i++)
        {
            if (_dragging && i == _dragIndex) continue;
            DrawStep(g, m.Steps[i], NodeRect(i), i, title, small, badgeFont, 255);
        }
        if (n == 0)
        {
            var r = NodeRect(1);
            using var dash = new Pen(DarkTheme.Outline, 1.5f * k) { DashStyle = DashStyle.Dash };
            using var path = Rounded(r, 8 * k);
            g.DrawPath(dash, path);
            DrawText(g, "No steps yet: add them with the buttons on the left", small, r, DarkTheme.SubtleText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // End block
        {
            var r = NodeRect(n);
            using var path = Rounded(r, r.Height / 2f);
            using var fill = new SolidBrush(DarkTheme.Field);
            g.FillPath(fill, path);
            DrawSelection(g, path, n);
            string text = m.RepeatText + (m.Steps.Count > 0 ? $"   ·   {m.DurationMs} ms per run" : "");
            DrawText(g, text, title, r, DarkTheme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // Dragged step follows the mouse, with a marker where it will land.
        if (_dragging && _dragIndex >= 0 && _dragIndex < n)
        {
            int target = Math.Clamp(_dropIndex, 0, n);
            var at = NodeRect(target);
            float y = at.Top - Gap / 2f;
            using var mark = new Pen(DarkTheme.Primary, 3f * k);
            g.DrawLine(mark, at.X, y, at.Right, y);
            var r = NodeRect(_dragIndex);
            r.Y = _dragY - NodeH / 2;
            DrawStep(g, m.Steps[_dragIndex], r, _dragIndex, title, small, badgeFont, 200);
        }
    }

    void DrawSelection(Graphics g, GraphicsPath path, int index)
    {
        if (index != _selected) return;
        using var pen = new Pen(DarkTheme.Primary, 2.5f * K);
        g.DrawPath(pen, path);
    }

    void DrawStep(Graphics g, MacroStep s, Rectangle r, int index, Font title, Font small, Font badgeFont, int alpha)
    {
        float k = K;
        var color = KindColor(s.Kind);
        using var path = Rounded(r, 9 * k);
        using var fill = new SolidBrush(Color.FromArgb(alpha, DarkTheme.Surface));
        g.FillPath(fill, path);
        // Colored band with the step type
        var band = new RectangleF(r.X, r.Y, 62 * k, r.Height);
        var clip = g.Clip;
        g.SetClip(path);
        using (var bandBrush = new SolidBrush(Color.FromArgb(alpha, color)))
            g.FillRectangle(bandBrush, band);
        g.Clip = clip;
        DrawText(g, KindBadge(s.Kind), badgeFont, Rectangle.Round(band), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using (var outline = new Pen(Color.FromArgb(alpha, DarkTheme.Border), 1.2f * k)) g.DrawPath(outline, path);
        DrawSelection(g, path, index);

        var textRect = new Rectangle((int)band.Right + (int)(12 * k), r.Y, r.Right - (int)band.Right - (int)(20 * k), r.Height);
        string detail = s.Detail;
        if (detail.Length == 0)
            DrawText(g, s.Title, title, textRect, DarkTheme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        else
        {
            var top = textRect with { Height = r.Height / 2 + (int)(2 * k) };
            var bottom = textRect with { Y = r.Y + r.Height / 2, Height = r.Height / 2 - (int)(4 * k) };
            DrawText(g, s.Title, title, top, DarkTheme.Text, TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
            DrawText(g, detail, small, bottom, DarkTheme.SubtleText, TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
        // Step number
        DrawText(g, (index + 1).ToString(), small, new Rectangle(r.Right - (int)(34 * k), r.Y, (int)(28 * k), (int)(22 * k)),
            DarkTheme.Outline, TextFormatFlags.Right);
    }

    int HitTest(Point client)
    {
        if (_macro == null) return int.MinValue;
        var p = new Point(client.X - AutoScrollPosition.X, client.Y - AutoScrollPosition.Y);
        for (int i = -1; i <= _macro.Steps.Count; i++)
            if (NodeRect(i).Contains(p)) return i;
        return int.MinValue;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int hit = HitTest(e.Location);
        if (hit == int.MinValue) return;
        _selected = hit;
        Invalidate();
        SelectionChanged?.Invoke();
        if (e.Button == MouseButtons.Left && hit >= 0 && hit < _macro!.Steps.Count)
        {
            _dragIndex = hit;
            _dragStart = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex < 0 || e.Button != MouseButtons.Left) return;
        if (!_dragging && Math.Abs(e.Y - _dragStart.Y) < 6 * K) return;
        _dragging = true;
        Cursor = Cursors.SizeNS;
        _dragY = e.Y - AutoScrollPosition.Y;
        // Landing slot: the gap nearest the pointer.
        int n = _macro!.Steps.Count;
        _dropIndex = Math.Clamp((int)Math.Round((_dragY - NodeRect(0).Top + Gap / 2.0) / (NodeH + Gap)), 0, n);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging && _macro != null)
        {
            int from = _dragIndex;
            int to = _dropIndex > from ? _dropIndex - 1 : _dropIndex;
            if (to != from && from >= 0 && from < _macro.Steps.Count)
            {
                var step = _macro.Steps[from];
                _macro.Steps.RemoveAt(from);
                _macro.Steps.Insert(Math.Clamp(to, 0, _macro.Steps.Count), step);
                _selected = Math.Clamp(to, 0, _macro.Steps.Count - 1);
                StepsReordered?.Invoke();
                SelectionChanged?.Invoke();
            }
        }
        _dragging = false;
        _dragIndex = -1;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_macro == null) return;
        if (e.KeyCode == Keys.Up) { Selected = _selected - 1; SelectionChanged?.Invoke(); e.Handled = true; }
        if (e.KeyCode == Keys.Down) { Selected = _selected + 1; SelectionChanged?.Invoke(); e.Handled = true; }
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }
}
