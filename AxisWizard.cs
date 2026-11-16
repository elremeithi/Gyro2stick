using System.Diagnostics;

namespace Gyro2stick;

/// <summary>
/// Finds which built-in gyro axes turn and tilt the aim: the user turns the device left, then tilts it to look up,
/// and the axis that moved most (with its direction) is taken for each.
/// </summary>
sealed class AxisWizard : Form
{
    const double StepSeconds = 3, MinDegrees = 20;

    readonly BuiltInGyro _gyro;
    readonly Label _text = new();
    readonly ProgressBar _progress = new();
    readonly Button _go = new(), _cancel = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 10 };
    readonly Stopwatch _clock = new();
    readonly double[] _sum = new double[3];
    double _last;
    int _step; // 0 intro, 1 turning, 2 tilting, 3 done

    public SensorAxis Turn { get; private set; }
    public SensorAxis Tilt { get; private set; }

    public AxisWizard(BuiltInGyro gyro)
    {
        _gyro = gyro;
        Text = "Detect built-in gyro axes";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None; // bounds below are in 96-dpi pixels, scaled at the end
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(440, 210);

        _text.SetBounds(16, 14, 408, 110);
        _progress.SetBounds(16, 130, 408, 14);
        _progress.Maximum = 1000;
        _go.SetBounds(228, 160, 96, 32);
        _cancel.SetBounds(330, 160, 94, 32);
        _cancel.Text = "Cancel";
        _cancel.DialogResult = DialogResult.Cancel;
        CancelButton = _cancel;
        AcceptButton = _go;
        _go.Click += (_, _) => Advance();
        _timer.Tick += (_, _) => Tick();
        Controls.AddRange(new Control[] { _text, _progress, _go, _cancel });
        DarkTheme.Apply(this);
        BackColor = DarkTheme.Background;
        ForeColor = DarkTheme.Text;
        float k = DeviceDpi / 96f;
        if (k > 1.01f) Scale(new SizeF(k, k));
        ShowIntro();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTheme.ApplyTitleBar(this);
    }

    void ShowIntro(string? problem = null)
    {
        _step = 0;
        _timer.Stop();
        _progress.Value = 0;
        _text.Text = (problem != null ? problem + "\n\n" : "") +
            "Hold the device the way you play, then click Start.\n\n" +
            "1. Turn LEFT: rotate it (or yourself) slowly to the left, about a quarter turn.\n" +
            "2. Tilt to LOOK UP: the top edge comes toward you, the back faces the ceiling.";
        _go.Text = "Start";
        _go.Enabled = true;
    }

    void Advance()
    {
        if (_step == 3)
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        StartStep(1);
    }

    void StartStep(int step)
    {
        _step = step;
        Array.Clear(_sum);
        _clock.Restart();
        _last = 0;
        _go.Enabled = false;
        _text.Text = step == 1
            ? "Turn LEFT now…\n\nSlowly rotate the device to the left, about a quarter turn, as if looking left in a game."
            : "Tilt to LOOK UP now…\n\nTilt the top edge toward you (the back of the device turns toward the ceiling), as if looking up.";
        _timer.Start();
    }

    void Tick()
    {
        double t = _clock.Elapsed.TotalSeconds, dt = t - _last;
        _last = t;
        var (x, y, z) = _gyro.Read(out _);
        _sum[0] += x * dt;
        _sum[1] += y * dt;
        _sum[2] += z * dt;
        _progress.Value = (int)Math.Clamp(t / StepSeconds * 1000, 0, 1000);
        if (t < StepSeconds) return;

        _timer.Stop();
        var axis = Dominant();
        if (axis == null)
        {
            ShowIntro("No clear movement was measured. Move a bit further (about 45°) and keep the other axes steady.");
            return;
        }
        if (_step == 1)
        {
            Turn = axis.Value;
            StartStep(2);
            return;
        }
        if (BuiltInGyro.Index(axis.Value) == BuiltInGyro.Index(Turn))
        {
            ShowIntro("Turning and tilting moved the same sensor axis. Try again, turning flat and tilting straight up.");
            return;
        }
        Tilt = axis.Value;
        _step = 3;
        string Name(SensorAxis a) => new[] { "+X", "−X", "+Y", "−Y", "+Z", "−Z" }[(int)a];
        _text.Text = $"Detected:\n\nTurn = {Name(Turn)}\nTilt = {Name(Tilt)}\n\nClick Use, then Apply in the main window. " +
                     "The gyro recalibrates afterwards: keep the device still for a second.";
        _go.Text = "Use";
        _go.Enabled = true;
    }

    /// <summary>The axis that turned furthest, signed so this motion is positive; null if unclear.</summary>
    SensorAxis? Dominant()
    {
        var order = Enumerable.Range(0, 3).OrderByDescending(i => Math.Abs(_sum[i])).ToArray();
        double best = Math.Abs(_sum[order[0]]), second = Math.Abs(_sum[order[1]]);
        if (best < MinDegrees || second > best * 0.6) return null;
        return (SensorAxis)(order[0] * 2 + (_sum[order[0]] >= 0 ? 0 : 1));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
