using System.Drawing.Drawing2D;

namespace DSGyro360;

/// <summary>
/// Live square view of the right stick sent to the game (physical stick + gyro), with a short trail,
/// the physical stick alone as a hollow dot, and the gyro turn rate underneath.
/// </summary>
public sealed class GyroScope : Control
{
    static readonly Color Accent = Color.FromArgb(0, 200, 230); // the gyro ring in the app icon
    const int TrailLength = 40;

    readonly Func<LiveSample?> _source;
    readonly Queue<PointF> _trail = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    LiveSample? _sample;

    public GyroScope(Func<LiveSample?> source)
    {
        _source = source;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Tag = DarkTheme.KeepColors;
        BackColor = DarkTheme.Surface;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    void Tick()
    {
        if (!Visible || FindForm() is not { Visible: true, WindowState: not FormWindowState.Minimized }) return;
        _sample = _source();
        if (_sample == null) _trail.Clear();
        else
        {
            _trail.Enqueue(new PointF((float)_sample.OutX, (float)_sample.OutY));
            while (_trail.Count > TrailLength) _trail.Dequeue();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        using var border = new Pen(DarkTheme.Border);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        int textH = Font.Height;
        // Plot square: below the title line, above the two value lines.
        int size = Math.Min(Width - 16, Height - textH * 3 - 12);
        var plot = new Rectangle((Width - size) / 2, textH + 6, size, size);
        float cx = plot.X + plot.Width / 2f, cy = plot.Y + plot.Height / 2f, r = plot.Width / 2f;
        PointF ToScreen(double x, double y) => new(cx + (float)x * r, cy - (float)y * r);

        using (var grid = new Pen(DarkTheme.Border))
        {
            g.DrawRectangle(grid, plot);
            g.DrawLine(grid, cx, plot.Top, cx, plot.Bottom);
            g.DrawLine(grid, plot.Left, cy, plot.Right, cy);
            g.DrawEllipse(grid, cx - r, cy - r, 2 * r, 2 * r);
            grid.DashStyle = DashStyle.Dot;
            g.DrawEllipse(grid, cx - r / 2, cy - r / 2, r, r);
        }

        using var subtle = new SolidBrush(DarkTheme.SubtleText);
        using var text = new SolidBrush(DarkTheme.Text);
        g.DrawString("Right stick", Font, subtle, 6, 3);

        var s = _sample;
        if (s == null)
        {
            var msg = "No live data";
            var sz = g.MeasureString(msg, Font);
            g.DrawString(msg, Font, subtle, cx - sz.Width / 2, cy - sz.Height / 2);
            return;
        }

        string state = s.GyroActive ? "gyro ●" : "gyro idle";
        var stateSize = g.MeasureString(state, Font);
        using (var stateBrush = new SolidBrush(s.GyroActive ? Accent : DarkTheme.SubtleText))
            g.DrawString(state, Font, stateBrush, Width - stateSize.Width - 6, 3);

        var color = s.GyroActive ? Accent : DarkTheme.Text;
        int i = 0, n = _trail.Count;
        PointF? prev = null;
        foreach (var p in _trail)
        {
            var pt = ToScreen(p.X, p.Y);
            if (prev != null)
            {
                using var pen = new Pen(Color.FromArgb(20 + 160 * ++i / n, color), 2f);
                g.DrawLine(pen, prev.Value, pt);
            }
            prev = pt;
        }

        var stick = ToScreen(s.StickX, s.StickY);
        using (var hollow = new Pen(DarkTheme.SubtleText, 1.5f))
            g.DrawEllipse(hollow, stick.X - 5, stick.Y - 5, 10, 10);
        var outPt = ToScreen(s.OutX, s.OutY);
        using (var dot = new SolidBrush(color))
            g.FillEllipse(dot, outPt.X - 5, outPt.Y - 5, 10, 10);

        double mag = Math.Min(Math.Sqrt(s.OutX * s.OutX + s.OutY * s.OutY), 1);
        int y1 = plot.Bottom + 4;
        g.DrawString($"Gyro  X {s.RateX,6:0.0}°/s   Y {s.RateY,6:0.0}°/s", Font, text, 6, y1);
        g.DrawString($"Output {mag * 100,3:0}%   ({s.OutX * 100:0}, {s.OutY * 100:0})", Font, subtle, 6, y1 + textH);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
