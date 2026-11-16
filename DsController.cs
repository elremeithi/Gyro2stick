using System.Diagnostics;
using HidSharp;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Gyro2stick;

public enum PadKind { DualShock4, DualSense, Handheld }

/// <summary>
/// One frame of right-stick output: the physical stick, the final stick sent to the game (stick + gyro),
/// both -1..1 with up positive, and the gyro turn rate in deg/s.
/// </summary>
public sealed record LiveSample(double StickX, double StickY, double OutX, double OutY,
    double RateX, double RateY, bool GyroActive);

/// <summary>
/// One physical DualShock 4 / DualSense. Reads its HID reports on a dedicated thread and
/// feeds a virtual Xbox 360 controller (ViGEmBus), forwarding rumble back.
/// </summary>
public sealed class DsController : IDisposable
{
    public const int SonyVid = 0x054C;
    public static readonly Dictionary<int, PadKind> SupportedPids = new()
    {
        [0x05C4] = PadKind.DualShock4, // DS4 v1
        [0x09CC] = PadKind.DualShock4, // DS4 v2
        [0x0BA0] = PadKind.DualShock4, // DS4 USB wireless adapter
        [0x0CE6] = PadKind.DualSense,
        [0x0DF2] = PadKind.DualSense,  // DualSense Edge
    };

    /// <summary>
    /// Third-party pads whose PS4 / PS5 modes speak the DualShock 4 / DualSense USB protocol under their own
    /// vendor ID. Both modes use a 64-byte input report; the output (rumble) report tells them apart.
    /// </summary>
    static readonly Dictionary<int, string> Ps4ModeVendors = new()
    {
        [0x146B] = "Nacon", // Bigben / older Nacon Revolution pads
        [0x3285] = "Nacon", // Nacon Revolution 5 Pro and newer
    };

    /// <summary>Recognises a supported pad. Only the gamepad collection qualifies (64-byte DS4 input report).</summary>
    public static bool TryIdentify(HidDevice d, out PadKind kind, out string name)
    {
        kind = PadKind.DualShock4;
        name = "";
        if (d.VendorID == SonyVid)
        {
            if (!SupportedPids.TryGetValue(d.ProductID, out kind)) return false;
            name = kind == PadKind.DualSense ? "DualSense" : "DualShock 4";
            return true;
        }
        if (!Ps4ModeVendors.TryGetValue(d.VendorID, out var vendor) || d.GetMaxInputReportLength() != 64) return false;
        switch (d.GetMaxOutputReportLength())
        {
            case 32:
                name = vendor + " (PS4)";
                return true;
            case 48:
                kind = PadKind.DualSense;
                name = vendor + " (PS5)";
                return true;
            default:
                return false;
        }
    }

    public static bool IsKnownVendor(int vid) => vid == SonyVid || Ps4ModeVendors.ContainsKey(vid);

    // Raw gyro counts per degree/second (both pads use a +/-2000 dps range on int16).
    const double GyroCountsPerDps = 16.4;
    const int CalibrationMs = 1000;

    public string DevicePath { get; }
    public PadKind Kind { get; }
    public bool Bluetooth { get; }
    public string? InstanceId { get; set; }

    // Live status for the UI.
    public int Slot { get; private set; } = -1;
    public int Battery { get; private set; } = -1;
    public bool Charging { get; private set; }
    public bool GyroActive { get; private set; }
    public string Status { get; private set; } = "Starting";
    public bool IsDead { get; private set; }
    /// <summary>Latest right-stick output, for the live view. Replaced every report, null until the first.</summary>
    public LiveSample? Live { get; private set; }

    double _rateX, _rateY; // gyro turn rate in deg/s, screen directions (right / up positive)

    readonly HidDevice? _device;
    readonly HidStream? _stream;
    readonly ViGEmClient _client;
    readonly AppSettings _settings;
    readonly Thread _thread;
    readonly object _writeLock = new();
    readonly int _outLen;
    volatile bool _running = true;
    IXbox360Controller? _x360;
    byte _btSeq;
    byte _lastLarge, _lastSmall;

    // Gyro bias calibration (controller must be still).
    double _biasX, _biasY, _biasZ;
    double _sumX, _sumY, _sumZ;
    int _calCount;
    long _calStart = -1;
    volatile bool _calRequested = true;

    // Rumble shakes the gyro: a false yaw turn (mostly from the large motor) plus jitter.
    static readonly double[] RumbleLevelPoints = { 0, 64, 128, 192, 255 };
    // Measured on a DualSense lying on a table; used until the user runs "Calibrate rumble drift".
    static readonly double[] DefaultDualSenseRumbleComp =
    {
        0, 0,  0, 0,  0, -0.4,  0, -3.0,  0, -6.2, // large motor
        0, 0,  0, 0,  0, 0,     0, 0,     0, 0,    // small motor
    };
    const double RumbleSmoothingTau = 0.012; // ~13 Hz low-pass, only while rumbling
    double _effLarge, _effSmall; // motor speed estimate (motors don't spin up/down instantly)
    double _lpPitch, _lpYaw;
    long _lastGyroTicks;

    // Rumble calibration: baseline, then large motor at 4 levels, then small motor at 4 levels.
    const int RumbleSettleMs = 400, RumbleMeasureMs = 1000, RumbleCalSteps = 9;
    volatile bool _rumbleCalRequested;
    bool _rumbleCalActive;
    int _rcStep;
    long _rcStepStart;
    double _rcSumPitch, _rcSumYaw;
    int _rcCount;
    readonly double[] _rcPitch = new double[RumbleCalSteps], _rcYaw = new double[RumbleCalSteps];

    public DsController(HidDevice device, PadKind kind, string name, ViGEmClient client, AppSettings settings)
    {
        _device = device;
        _client = client;
        _settings = settings;
        DevicePath = device.DevicePath;
        Kind = kind;
        DisplayName = name;
        _thirdParty = device.VendorID != SonyVid;
        Bluetooth = device.GetMaxInputReportLength() > 64;
        _outLen = Math.Max(device.GetMaxOutputReportLength(), Bluetooth ? 78 : (kind == PadKind.DualSense ? 48 : 32));

        var config = new OpenConfiguration();
        config.SetOption(OpenOption.Priority, OpenPriority.High);
        _stream = device.Open(config);
        _stream.ReadTimeout = 1000;
        _stream.WriteTimeout = 250;

        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "DS " + DevicePath, Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>
    /// A handheld's built-in XInput controller (GPD Win 4 and similar), with the gyro read from the Windows motion
    /// sensor. <paramref name="otherSlots"/> gives the XInput slots of the virtual pads (null while one is still unknown),
    /// so the built-in pad can be told apart from them.
    /// </summary>
    public DsController(Handheld.Pad pad, BuiltInGyro gyro, Func<ISet<int>?> otherSlots, ViGEmClient client, AppSettings settings)
    {
        _builtIn = pad;
        _gyro = gyro;
        _otherSlots = otherSlots;
        _client = client;
        _settings = settings;
        DevicePath = pad.InterfacePath;
        InstanceId = pad.InstanceId;
        Kind = PadKind.Handheld;
        DisplayName = Handheld.DeviceName;
        _lastTurn = settings.BuiltInTurnAxis;
        _lastTilt = settings.BuiltInTiltAxis;
        _thread = new Thread(BuiltInLoop) { IsBackground = true, Name = "Built-in pad", Priority = ThreadPriority.AboveNormal };
    }

    public string DisplayName { get; }
    readonly bool _thirdParty;

    // ---------- built-in handheld controller ----------
    readonly Handheld.Pad? _builtIn;
    readonly BuiltInGyro? _gyro;
    readonly Func<ISet<int>?>? _otherSlots;
    volatile int _xSlot = -1;
    int _lastOwnSlot = -1;
    long _ownSlotFreedAt;
    SensorAxis _lastTurn, _lastTilt;
    const int BuiltInRateHz = 250;

    /// <summary>True while this pad's virtual Xbox 360 controller is plugged in.</summary>
    public bool HasVirtual => _x360 != null;

    void BuiltInLoop()
    {
        timeBeginPeriod(1);
        var state = new PadState();
        long lastSearch = 0, lastBattery = 0;
        long period = Stopwatch.Frequency / BuiltInRateHz, next = Stopwatch.GetTimestamp();
        try
        {
            while (_running)
            {
                long ts = Stopwatch.GetTimestamp();
                if (ts < next)
                {
                    Thread.Sleep(1);
                    continue;
                }
                next = Math.Max(next + period, ts);
                long now = Environment.TickCount64;

                if (_xSlot < 0)
                {
                    if (now - lastSearch < 500) continue;
                    lastSearch = now;
                    _xSlot = FindSourceSlot();
                    if (_xSlot < 0)
                    {
                        Status = "Waiting for the built-in controller (mouse mode?)";
                        Live = null;
                        continue;
                    }
                }
                if (!XInput.TryGetState(_xSlot, out var x))
                {
                    _xSlot = -1;
                    lastSearch = now;
                    ForgetOwnSlot();
                    DisconnectVirtual();
                    continue;
                }
                if (now - lastBattery > 5000)
                {
                    lastBattery = now;
                    var power = SystemInformation.PowerStatus;
                    Battery = power.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery) ? -1
                        : (int)Math.Round(Math.Clamp(power.BatteryLifePercent, 0, 1) * 100);
                    Charging = power.PowerLineStatus == PowerLineStatus.Online && Battery is >= 0 and < 100;
                }
                FillBuiltIn(ref state, x, out bool gyroSilent);

                if (!_settings.Enabled)
                {
                    ForgetOwnSlot();
                    DisconnectVirtual();
                    GyroActive = false;
                    Live = null;
                    Status = "Off";
                    continue;
                }
                try
                {
                    EnsureVirtual();
                    Submit(ref state);
                    if (gyroSilent && Status == "Active") Status = "Active - gyro sensor not reporting";
                }
                catch (Exception ex)
                {
                    Status = "ViGEm error: " + ex.Message;
                    DisconnectVirtual();
                }
            }
        }
        finally
        {
            timeEndPeriod(1);
            DisconnectVirtual();
            IsDead = true;
            Status = "Disconnected";
        }
    }

    /// <summary>Remembers the slot of our own virtual pad as it goes away, so the search doesn't pick it while it lingers.</summary>
    void ForgetOwnSlot()
    {
        if (_x360 == null || Slot < 0) return;
        _lastOwnSlot = Slot;
        _ownSlotFreedAt = Environment.TickCount64;
    }

    /// <summary>The XInput slot of the built-in pad: connected, same IDs, and not one of the virtual pads.</summary>
    int FindSourceSlot()
    {
        var others = _otherSlots!();
        if (others == null) return -1; // a virtual pad's slot isn't known yet
        bool ownLingers = _lastOwnSlot >= 0 && Environment.TickCount64 - _ownSlotFreedAt < 2000;
        for (int i = 0; i < 4; i++)
        {
            if (others.Contains(i) || (ownLingers && i == _lastOwnSlot)) continue;
            if (!XInput.TryGetState(i, out _)) continue;
            var ids = XInput.GetIds(i);
            if (ids != null && _builtIn!.Vid != 0 && ids.Value != (_builtIn.Vid, _builtIn.Pid)) continue;
            return i;
        }
        return -1;
    }

    static byte StickByte(short v) => (byte)((v + 32768) >> 8);
    static short GyroCounts(double dps) => (short)Math.Clamp(Math.Round(dps * GyroCountsPerDps), -32768, 32767);

    /// <summary>XInput state + Windows gyro into the same state a DualShock report gives.</summary>
    void FillBuiltIn(ref PadState s, in XInput.State x, out bool gyroSilent)
    {
        s.LX = StickByte(x.ThumbLX);
        s.LY = (byte)(255 - StickByte(x.ThumbLY)); // XInput: up positive; DS report: up is 0
        s.RX = StickByte(x.ThumbRX);
        s.RY = (byte)(255 - StickByte(x.ThumbRY));
        s.L2 = x.LeftTrigger;
        s.R2 = x.RightTrigger;
        s.L2Btn = s.R2Btn = false;

        ushort b = x.Buttons;
        bool On(ushort mask) => (b & mask) != 0;
        s.Cross = On(XInput.A);
        s.Circle = On(XInput.B);
        s.Square = On(XInput.X);
        s.Triangle = On(XInput.Y);
        s.L1 = On(XInput.LeftShoulder);
        s.R1 = On(XInput.RightShoulder);
        s.Share = On(XInput.Back);
        s.Options = On(XInput.Start);
        s.L3 = On(XInput.LeftThumb);
        s.R3 = On(XInput.RightThumb);
        s.PS = On(XInput.Guide);
        s.TouchClick = s.Touching = false;

        bool up = On(XInput.DpadUp), down = On(XInput.DpadDown), left = On(XInput.DpadLeft), right = On(XInput.DpadRight);
        s.Dpad = (up, right, down, left) switch
        {
            (true, false, false, false) => 0,
            (true, true, false, false) => 1,
            (false, true, false, false) => 2,
            (false, true, true, false) => 3,
            (false, false, true, false) => 4,
            (false, false, true, true) => 5,
            (false, false, false, true) => 6,
            (true, false, false, true) => 7,
            _ => 8,
        };

        // Changing the axes changes what each bias belongs to: measure it again.
        var st = _settings;
        if (st.BuiltInTurnAxis != _lastTurn || st.BuiltInTiltAxis != _lastTilt)
        {
            _lastTurn = st.BuiltInTurnAxis;
            _lastTilt = st.BuiltInTiltAxis;
            _calRequested = true;
        }
        var (gx, gy, gz) = _gyro!.Read(out long age);
        gyroSilent = age == long.MaxValue || age > 2000;
        int turn = BuiltInGyro.Index(_lastTurn), tilt = BuiltInGyro.Index(_lastTilt);
        int roll = turn != tilt ? 3 - turn - tilt : (turn + 1) % 3;
        double[] raw = { gx, gy, gz };
        s.GX = GyroCounts(BuiltInGyro.Pick(_lastTilt, gx, gy, gz)); // pitch: tilt to look up = positive
        s.GY = GyroCounts(BuiltInGyro.Pick(_lastTurn, gx, gy, gz)); // yaw: turn left = positive
        s.GZ = GyroCounts(raw[roll]);
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    public void Start() => _thread.Start();

    public void RequestCalibration() => _calRequested = true;

    /// <summary>Measures how much each motor disturbs the gyro. Controller must lie still on a table.</summary>
    public void RequestRumbleCalibration() => _rumbleCalRequested = true;

    /// <summary>
    /// Over Bluetooth both pads send a reduced report until a calibration feature report is read;
    /// reading it switches them to the full report that includes motion data.
    /// </summary>
    void EnableFullReports()
    {
        if (!Bluetooth) return;
        try
        {
            var buf = new byte[Math.Max(_device!.GetMaxFeatureReportLength(), 64)];
            buf[0] = Kind == PadKind.DualSense ? (byte)0x05 : (byte)0x02;
            _stream!.GetFeature(buf);
        }
        catch { /* retried while reports stay reduced */ }
    }

    void ReadLoop()
    {
        var buf = new byte[Math.Max(_device!.GetMaxInputReportLength(), 64)];
        var stream = _stream!;
        var state = new PadState();
        int timeouts = 0;
        long lastModeRequest = 0;
        EnableFullReports();

        while (_running)
        {
            int n;
            try
            {
                n = stream.Read(buf, 0, buf.Length);
            }
            catch (TimeoutException)
            {
                if (++timeouts >= 3)
                {
                    DisconnectVirtual();
                    Status = "No input";
                }
                continue;
            }
            catch
            {
                break; // unplugged / powered off
            }
            timeouts = 0;

            if (!Parse(buf, n, ref state))
            {
                // Still in reduced Bluetooth mode: ask again (at most once a second).
                if (Environment.TickCount64 - lastModeRequest > 1000)
                {
                    lastModeRequest = Environment.TickCount64;
                    EnableFullReports();
                }
                continue;
            }

            if (!_settings.Enabled)
            {
                DisconnectVirtual();
                GyroActive = false;
                Live = null;
                Status = "Off";
                continue;
            }

            try
            {
                EnsureVirtual();
                Submit(ref state);
            }
            catch (Exception ex)
            {
                Status = "ViGEm error: " + ex.Message;
                DisconnectVirtual();
            }
        }

        DisconnectVirtual();
        IsDead = true;
        Status = "Disconnected";
    }

    internal struct PadState
    {
        public byte LX, LY, RX, RY, L2, R2, Dpad;
        public bool Square, Cross, Circle, Triangle, L1, R1, L2Btn, R2Btn, Share, Options, L3, R3, PS, TouchClick, Touching;
        public short GX, GY, GZ;
    }

    static short I16(byte[] d, int i) => (short)(d[i] | (d[i + 1] << 8));

    bool Parse(byte[] d, int n, ref PadState s)
    {
        int b; // offset of the left-stick X byte
        if (Kind == PadKind.DualShock4)
        {
            if (!Bluetooth && d[0] == 0x01 && n >= 64) b = 1;
            else if (Bluetooth && d[0] == 0x11 && n >= 78) b = 3;
            else return false;
        }
        else
        {
            if (!Bluetooth && d[0] == 0x01 && n >= 64) b = 1;
            else if (Bluetooth && d[0] == 0x31 && n >= 78) b = 2;
            else return false;
        }

        s.LX = d[b]; s.LY = d[b + 1]; s.RX = d[b + 2]; s.RY = d[b + 3];
        int btn; // offset of the dpad/face-button byte
        if (Kind == PadKind.DualShock4)
        {
            btn = b + 4;
            s.L2 = d[b + 7]; s.R2 = d[b + 8];
            s.GX = I16(d, b + 12); s.GY = I16(d, b + 14); s.GZ = I16(d, b + 16);
            byte bat = d[b + 29];
            int level = bat & 0x0F;
            bool cable = (bat & 0x10) != 0;
            // Third-party pads often leave the battery byte at 0 when wired: show it as unknown.
            Battery = _thirdParty && bat == 0 ? -1 : Math.Min(cable ? level * 10 : level * 10 + 5, 100);
            Charging = cable && level < 11;
            s.Touching = (d[b + 34] & 0x80) == 0;
        }
        else
        {
            btn = b + 7;
            s.L2 = d[b + 4]; s.R2 = d[b + 5];
            s.GX = I16(d, b + 15); s.GY = I16(d, b + 17); s.GZ = I16(d, b + 19);
            s.Touching = (d[b + 32] & 0x80) == 0;
            byte st = d[b + 52];
            int charge = st >> 4;
            Battery = _thirdParty && st == 0 ? -1 : charge == 2 ? 100 : Math.Min((st & 0x0F) * 10 + 5, 100);
            Charging = charge == 1;
        }

        byte b0 = d[btn], b1 = d[btn + 1], b2 = d[btn + 2];
        s.Dpad = (byte)(b0 & 0x0F);
        s.Square = (b0 & 0x10) != 0;
        s.Cross = (b0 & 0x20) != 0;
        s.Circle = (b0 & 0x40) != 0;
        s.Triangle = (b0 & 0x80) != 0;
        s.L1 = (b1 & 0x01) != 0;
        s.R1 = (b1 & 0x02) != 0;
        s.L2Btn = (b1 & 0x04) != 0;
        s.R2Btn = (b1 & 0x08) != 0;
        s.Share = (b1 & 0x10) != 0;
        s.Options = (b1 & 0x20) != 0;
        s.L3 = (b1 & 0x40) != 0;
        s.R3 = (b1 & 0x80) != 0;
        s.PS = (b2 & 0x01) != 0;
        s.TouchClick = (b2 & 0x02) != 0;
        return true;
    }

    void EnsureVirtual()
    {
        if (_x360 != null) return;
        var pad = _client.CreateXbox360Controller();
        pad.AutoSubmitReport = false;
        pad.FeedbackReceived += OnFeedback;
        pad.Connect();
        _x360 = pad;
        Slot = -1;
    }

    void DisconnectVirtual()
    {
        ReleaseHeldKeys(); // never leave Alt etc. stuck down
        _macroEngine?.StopAll();
        _remapEngine?.ReleaseAll();
        _remapActive = false;
        _psDown = false;
        var pad = _x360;
        if (pad == null) return;
        _x360 = null;
        Slot = -1;
        try { pad.FeedbackReceived -= OnFeedback; pad.Disconnect(); } catch { }
        SendRumble(0, 0);
    }

    void OnFeedback(object? sender, Xbox360FeedbackReceivedEventArgs e)
    {
        Slot = e.LedNumber;
        if (_rumbleCalActive) return; // calibration owns the motors
        if (_macroEngine?.Busy == true) return; // a macro's rumble step is playing
        double k = _settings.Rumble ? Math.Clamp(_settings.RumbleStrength, 0, 1) : 0;
        SendRumble((byte)Math.Round(e.LargeMotor * k), (byte)Math.Round(e.SmallMotor * k));
    }

    static short StickAxis(double v) => (short)Math.Clamp(Math.Round(v * 32767.0), -32768, 32767);

    /// <summary>Raw 0..255 stick byte to -1..1 (Y flipped so up is positive, like XInput).</summary>
    static (double x, double y) StickToUnit(byte rx, byte ry) =>
        ((rx - 127.5) / 127.5, -(ry - 127.5) / 127.5);

    /// <summary>
    /// Radial deadzone, rescaled so output still reaches 1. Anti-deadzone makes the output start at
    /// that value (instead of 0) as soon as the stick leaves the deadzone.
    /// </summary>
    static (double x, double y) ApplyRadialDeadzone(double x, double y, double dz, double antiDz)
    {
        // With anti-deadzone, resting stick noise would jump straight to antiDz: keep a small floor.
        if (antiDz > 0) dz = Math.Max(dz, 0.03);
        double m = Math.Sqrt(x * x + y * y);
        if (m <= dz || m == 0) return (0, 0);
        double scaled = Math.Min((m - dz) / (1 - dz), 1);
        scaled = antiDz + (1 - antiDz) * scaled;
        return (x / m * scaled, y / m * scaled);
    }

    // PS-button combos (screenshot / recording / shortcuts).
    bool _psDown, _chordUsed;
    long _psDownAt, _guideUntil;
    readonly HashSet<ChordButton> _swallowed = new();
    // Modifier keys of a shortcut stay down while PS is held, so PS + Cross, Cross, Cross steps through Alt+Tab.
    readonly List<ushort> _heldKeys = new();
    const int GuideHoldMs = 400, GuideTapMs = 100;

    static bool Pressed(ref PadState s, ChordButton b) => b switch
    {
        ChordButton.Create => s.Share,
        ChordButton.Options => s.Options,
        ChordButton.TouchpadClick => s.TouchClick,
        ChordButton.Triangle => s.Triangle,
        ChordButton.Circle => s.Circle,
        ChordButton.Cross => s.Cross,
        ChordButton.Square => s.Square,
        ChordButton.L1 => s.L1,
        ChordButton.R1 => s.R1,
        ChordButton.L2 => s.L2Btn || s.L2 > 30,
        ChordButton.R2 => s.R2Btn || s.R2 > 30,
        ChordButton.L3 => s.L3,
        ChordButton.R3 => s.R3,
        ChordButton.DpadUp => s.Dpad is 7 or 0 or 1,
        ChordButton.DpadRight => s.Dpad is >= 1 and <= 3,
        ChordButton.DpadDown => s.Dpad is >= 3 and <= 5,
        ChordButton.DpadLeft => s.Dpad is >= 5 and <= 7,
        _ => false,
    };

    static void Release(ref PadState s, ChordButton b)
    {
        switch (b)
        {
            case ChordButton.Create: s.Share = false; break;
            case ChordButton.Options: s.Options = false; break;
            case ChordButton.TouchpadClick: s.TouchClick = false; break;
            case ChordButton.Triangle: s.Triangle = false; break;
            case ChordButton.Circle: s.Circle = false; break;
            case ChordButton.Cross: s.Cross = false; break;
            case ChordButton.Square: s.Square = false; break;
            case ChordButton.L1: s.L1 = false; break;
            case ChordButton.R1: s.R1 = false; break;
            case ChordButton.L2: s.L2Btn = false; s.L2 = 0; break;
            case ChordButton.R2: s.R2Btn = false; s.R2 = 0; break;
            case ChordButton.L3: s.L3 = false; break;
            case ChordButton.R3: s.R3 = false; break;
            case ChordButton.DpadUp or ChordButton.DpadRight or ChordButton.DpadDown or ChordButton.DpadLeft:
                s.Dpad = 8; break; // released
        }
    }

    /// <summary>Mouse-button and gamepad shortcuts, held until their combo button is let go.</summary>
    readonly Dictionary<ChordButton, Shortcut> _shortcutHolds = new();

    void RunShortcut(Shortcut sc)
    {
        if (sc.Kind == ShortcutKind.Run)
        {
            ShortcutRunner.Launch(sc.Target);
            return;
        }
        if (sc.Kind == ShortcutKind.Mouse)
        {
            MouseInput.Shortcut(sc.Mouse, down: true);
            if (sc.Mouse is not (ShortcutMouse.WheelUp or ShortcutMouse.WheelDown)) _shortcutHolds[sc.Button] = sc;
            return;
        }
        if (sc.Kind == ShortcutKind.Pad)
        {
            _shortcutHolds[sc.Button] = sc;
            return;
        }
        var combo = KeyCombo.Parse(sc.Keys);
        if (combo == null) return;
        if (combo.Key == null)
        {
            KeyInput.Tap(combo.Modifiers); // modifiers alone, e.g. "Win": a tap, not a hold
            return;
        }
        foreach (var m in combo.Modifiers.Where(m => !_heldKeys.Contains(m)))
        {
            KeyInput.Down(m);
            _heldKeys.Add(m);
        }
        KeyInput.Tap(combo.Key.Value);
    }

    void ReleaseHeldKeys()
    {
        for (int i = _heldKeys.Count - 1; i >= 0; i--) KeyInput.Up(_heldKeys[i]);
        _heldKeys.Clear();
    }

    /// <summary>
    /// PS + button runs a capture shortcut, like on a PS5. The game sees neither button: Guide is only sent when
    /// PS is tapped alone (or held alone), and the combo button stays released until it is let go.
    /// </summary>
    void HandleChords(ref PadState s)
    {
        var st = _settings;
        var shortcuts = st.Shortcuts;
        if (shortcuts.Count == 0) return;
        long now = Environment.TickCount64;

        if (s.PS)
        {
            if (!_psDown)
            {
                _psDown = true;
                _chordUsed = false;
                _psDownAt = now;
            }
            foreach (var sc in shortcuts)
            {
                if (sc.Button == ChordButton.None || !Pressed(ref s, sc.Button) || !_swallowed.Add(sc.Button)) continue;
                _chordUsed = true;
                RunShortcut(sc);
            }
            // Holding PS alone still holds Guide (e.g. Game Bar), a combo never does.
            s.PS = !_chordUsed && now - _psDownAt >= GuideHoldMs;
        }
        else if (_psDown)
        {
            _psDown = false;
            ReleaseHeldKeys();
            if (!_chordUsed && now - _psDownAt < GuideHoldMs) _guideUntil = now + GuideTapMs;
        }
        if (now < _guideUntil) s.PS = true;

        foreach (var b in _swallowed.ToList())
        {
            if (Pressed(ref s, b)) Release(ref s, b);
            else
            {
                _swallowed.Remove(b);
                if (_shortcutHolds.Remove(b, out var held) && held.Kind == ShortcutKind.Mouse)
                    MouseInput.Shortcut(held.Mouse, down: false);
            }
        }
        // Gamepad shortcuts press their Xbox button for as long as the combo button is held.
        foreach (var held in _shortcutHolds.Values)
            if (held.Kind == ShortcutKind.Pad) Press(ref s, held.Pad);
    }

    MacroEngine? _macroEngine;

    /// <summary>Runs the macros: hides blocked combo buttons and adds what running macros press.</summary>
    void ApplyMacros(ref PadState s)
    {
        var macros = _settings.Macros;
        if (macros.Count == 0 && _macroEngine == null) return;
        _macroEngine ??= new MacroEngine(SendRumble);
        var raw = s; // combos are read from the real buttons
        var released = new List<ChordButton>();
        var injected = new List<ChordButton>();
        var (buttons, sticks) = _macroEngine.Process(macros, b => Pressed(ref raw, b), released.Add, injected.Add);
        foreach (var b in released) Release(ref s, b);
        foreach (var b in injected) Press(ref s, b);
        foreach (var b in buttons) Press(ref s, b);
        foreach (var (side, x, y) in sticks)
        {
            byte bx = (byte)Math.Clamp(Math.Round(127.5 + x * 1.275), 0, 255);
            byte by = (byte)Math.Clamp(Math.Round(127.5 - y * 1.275), 0, 255); // report: up is 0
            if (side == MacroStick.Left) { s.LX = bx; s.LY = by; }
            else { s.RX = bx; s.RY = by; }
        }
    }

    static void Press(ref PadState s, ChordButton b)
    {
        switch (b)
        {
            case ChordButton.Create: s.Share = true; break;
            case ChordButton.Options: s.Options = true; break;
            case ChordButton.TouchpadClick: s.TouchClick = true; break;
            case ChordButton.Triangle: s.Triangle = true; break;
            case ChordButton.Circle: s.Circle = true; break;
            case ChordButton.Cross: s.Cross = true; break;
            case ChordButton.Square: s.Square = true; break;
            case ChordButton.L1: s.L1 = true; break;
            case ChordButton.R1: s.R1 = true; break;
            case ChordButton.L2: s.L2Btn = true; s.L2 = 255; break;
            case ChordButton.R2: s.R2Btn = true; s.R2 = 255; break;
            case ChordButton.L3: s.L3 = true; break;
            case ChordButton.R3: s.R3 = true; break;
            case ChordButton.DpadUp: AddDpad(ref s, up: true); break;
            case ChordButton.DpadDown: AddDpad(ref s, down: true); break;
            case ChordButton.DpadLeft: AddDpad(ref s, left: true); break;
            case ChordButton.DpadRight: AddDpad(ref s, right: true); break;
        }
    }

    static void Press(ref PadState s, MacroButton b)
    {
        switch (b)
        {
            case MacroButton.A: s.Cross = true; break;
            case MacroButton.B: s.Circle = true; break;
            case MacroButton.X: s.Square = true; break;
            case MacroButton.Y: s.Triangle = true; break;
            case MacroButton.LB: s.L1 = true; break;
            case MacroButton.RB: s.R1 = true; break;
            case MacroButton.LT: s.L2Btn = true; s.L2 = 255; break;
            case MacroButton.RT: s.R2Btn = true; s.R2 = 255; break;
            case MacroButton.Back: s.Share = true; break;
            case MacroButton.Start: s.Options = true; break;
            case MacroButton.LS: s.L3 = true; break;
            case MacroButton.RS: s.R3 = true; break;
            case MacroButton.Guide: s.PS = true; break;
            case MacroButton.Up: AddDpad(ref s, up: true); break;
            case MacroButton.Down: AddDpad(ref s, down: true); break;
            case MacroButton.Left: AddDpad(ref s, left: true); break;
            case MacroButton.Right: AddDpad(ref s, right: true); break;
        }
    }

    /// <summary>Adds a direction to the D-pad hat (0..7 clockwise from up, 8 = released).</summary>
    static void AddDpad(ref PadState s, bool up = false, bool right = false, bool down = false, bool left = false)
    {
        int h = s.Dpad;
        up |= h is 7 or 0 or 1;
        right |= h is >= 1 and <= 3;
        down |= h is >= 3 and <= 5;
        left |= h is >= 5 and <= 7;
        if (up && down) down = false;
        if (left && right) right = false;
        s.Dpad = (up, right, down, left) switch
        {
            (true, false, false, false) => 0,
            (true, true, _, _) => 1,
            (false, true, false, false) => 2,
            (_, true, true, _) => 3,
            (false, false, true, false) => 4,
            (_, _, true, true) => 5,
            (false, false, false, true) => 6,
            (true, _, _, true) => 7,
            _ => 8,
        };
    }

    void Submit(ref PadState s)
    {
        var pad = _x360!;
        var st = _settings;
        HandleChords(ref s);
        ApplyMacros(ref s);

        // Remap: a keyboard-and-mouse game gets keys and mouse movement; the virtual pad stays idle meanwhile.
        if (RemapPresets.Find(st.RemapPreset) is { } preset)
        {
            if (!st.RemapOnlyInGame || Foreground.IsGame(preset))
            {
                SubmitRemapped(ref s, preset);
                return;
            }
            RemapEngine.Status = $"Waiting: {preset.Name} isn't the window in front, so the controller works as usual.";
        }
        else RemapEngine.Status = "";
        if (_remapActive)
        {
            _remapActive = false;
            _remapEngine?.ReleaseAll();
        }

        if (Slot < 0)
        {
            try { Slot = pad.UserIndex; } catch { /* not assigned yet */ }
        }

        // Face buttons: PlayStation layout to Xbox positions.
        pad.SetButtonState(Xbox360Button.A, s.Cross);
        pad.SetButtonState(Xbox360Button.B, s.Circle);
        pad.SetButtonState(Xbox360Button.X, s.Square);
        pad.SetButtonState(Xbox360Button.Y, s.Triangle);
        pad.SetButtonState(Xbox360Button.LeftShoulder, s.L1);
        pad.SetButtonState(Xbox360Button.RightShoulder, s.R1);
        pad.SetButtonState(Xbox360Button.Back, s.Share || s.TouchClick);
        pad.SetButtonState(Xbox360Button.Start, s.Options);
        pad.SetButtonState(Xbox360Button.LeftThumb, s.L3);
        pad.SetButtonState(Xbox360Button.RightThumb, s.R3);
        pad.SetButtonState(Xbox360Button.Guide, s.PS);

        // Hat value 0..7 clockwise from up, 8 = released.
        int h = s.Dpad;
        pad.SetButtonState(Xbox360Button.Up, h == 7 || h == 0 || h == 1);
        pad.SetButtonState(Xbox360Button.Right, h >= 1 && h <= 3);
        pad.SetButtonState(Xbox360Button.Down, h >= 3 && h <= 5);
        pad.SetButtonState(Xbox360Button.Left, h >= 5 && h <= 7);

        pad.SetSliderValue(Xbox360Slider.LeftTrigger, s.L2);
        pad.SetSliderValue(Xbox360Slider.RightTrigger, s.R2);

        var (lx, ly) = StickToUnit(s.LX, s.LY);
        (lx, ly) = ApplyRadialDeadzone(lx, ly, st.StickDeadzone, st.LeftStickAntiDeadzone);
        var (rx, ry) = StickToUnit(s.RX, s.RY);
        (rx, ry) = ApplyRadialDeadzone(rx, ry, st.StickDeadzone, st.RightStickAntiDeadzone);

        _rateX = _rateY = 0;
        var (gx, gy) = ProcessGyro(ref s);
        double stickX = rx, stickY = ry;
        rx = Math.Clamp(rx + gx, -1, 1);
        ry = Math.Clamp(ry + gy, -1, 1);
        Live = new LiveSample(stickX, stickY, rx, ry, _rateX, _rateY, GyroActive);

        pad.SetAxisValue(Xbox360Axis.LeftThumbX, StickAxis(lx));
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, StickAxis(ly));
        pad.SetAxisValue(Xbox360Axis.RightThumbX, StickAxis(rx));
        pad.SetAxisValue(Xbox360Axis.RightThumbY, StickAxis(ry));
        pad.SubmitReport();
    }

    RemapEngine? _remapEngine;
    bool _remapActive;

    void SubmitRemapped(ref PadState s, RemapPreset preset)
    {
        var st = _settings;
        var pad = _x360!;
        var (lx, ly) = StickToUnit(s.LX, s.LY);
        (lx, ly) = ApplyRadialDeadzone(lx, ly, st.StickDeadzone, 0);
        var (rx, ry) = StickToUnit(s.RX, s.RY);
        (rx, ry) = ApplyRadialDeadzone(rx, ry, st.StickDeadzone, 0);
        _rateX = _rateY = 0;
        ProcessGyro(ref s); // updates _rateX / _rateY (deg/s) and GyroActive
        bool gyro = st.GyroEnabled && (st.RemapGyroAlways || GyroActive);
        Live = new LiveSample(rx, ry, rx, ry, _rateX, _rateY, gyro);

        var buttons = new bool[Enum.GetValues<MacroButton>().Length];
        int h = s.Dpad;
        buttons[(int)MacroButton.A] = s.Cross;
        buttons[(int)MacroButton.B] = s.Circle;
        buttons[(int)MacroButton.X] = s.Square;
        buttons[(int)MacroButton.Y] = s.Triangle;
        buttons[(int)MacroButton.LB] = s.L1;
        buttons[(int)MacroButton.RB] = s.R1;
        buttons[(int)MacroButton.LT] = s.L2Btn || s.L2 > 60;
        buttons[(int)MacroButton.RT] = s.R2Btn || s.R2 > 60;
        buttons[(int)MacroButton.Back] = s.Share || s.TouchClick;
        buttons[(int)MacroButton.Start] = s.Options;
        buttons[(int)MacroButton.LS] = s.L3;
        buttons[(int)MacroButton.RS] = s.R3;
        buttons[(int)MacroButton.Up] = h == 7 || h == 0 || h == 1;
        buttons[(int)MacroButton.Right] = h >= 1 && h <= 3;
        buttons[(int)MacroButton.Down] = h >= 3 && h <= 5;
        buttons[(int)MacroButton.Left] = h >= 5 && h <= 7;

        _remapEngine ??= new RemapEngine();
        _remapEngine.Process(preset, st, new RemapInput
        {
            Buttons = buttons, LX = lx, LY = ly, RX = rx, RY = ry,
            GyroX = _rateX, GyroY = _rateY, GyroActive = gyro,
        });
        RemapEngine.Status = $"Remapping for {preset.Name}.";
        if (!_remapActive)
        {
            _remapActive = true;
            pad.ResetReport(); // the game shouldn't also see a controller
            pad.SubmitReport();
        }
    }

    /// <summary>Returns the gyro contribution to the right stick, each axis in -1..1.</summary>
    (double x, double y) ProcessGyro(ref PadState s)
    {
        var st = _settings;

        long now = Stopwatch.GetTimestamp();
        double dt = _lastGyroTicks == 0 ? 0.004
            : Math.Clamp((now - _lastGyroTicks) / (double)Stopwatch.Frequency, 0.0005, 0.05);
        _lastGyroTicks = now;
        _effLarge = FollowMotor(_effLarge, _lastLarge, dt);
        _effSmall = FollowMotor(_effSmall, _lastSmall, dt);

        if (_rumbleCalRequested)
        {
            _rumbleCalRequested = false;
            _rumbleCalActive = true;
            _calStart = -1;
            StartRumbleCalStep(0);
        }
        if (_rumbleCalActive)
        {
            RumbleCalibrationTick(ref s);
            GyroActive = false;
            return (0, 0);
        }

        if (_calRequested)
        {
            _calRequested = false;
            _calStart = Environment.TickCount64;
            _sumX = _sumY = _sumZ = 0;
            _calCount = 0;
        }
        if (_calStart >= 0)
        {
            _sumX += s.GX; _sumY += s.GY; _sumZ += s.GZ; _calCount++;
            if (Environment.TickCount64 - _calStart >= CalibrationMs)
            {
                _biasX = _sumX / _calCount;
                _biasY = _sumY / _calCount;
                _biasZ = _sumZ / _calCount;
                _calStart = -1;
            }
            Status = "Calibrating gyro - keep still";
            GyroActive = false;
            return (0, 0);
        }
        Status = "Active";

        // Pitch is rotation about the pad's X axis, yaw about its Y axis (points up out of the face).
        double pitch = (s.GX - _biasX) / GyroCountsPerDps;
        double yaw = (s.GY - _biasY) / GyroCountsPerDps;

        // Runs even while gyro aiming is inactive so the filter is settled when it activates.
        if (st.RumbleGyroFix && (_effLarge > 2 || _effSmall > 2))
        {
            var (cPitch, cYaw) = RumbleOffset();
            pitch -= cPitch;
            yaw -= cYaw;
            double a = 1 - Math.Exp(-dt / RumbleSmoothingTau);
            _lpPitch += a * (pitch - _lpPitch);
            _lpYaw += a * (yaw - _lpYaw);
            pitch = _lpPitch;
            yaw = _lpYaw;
        }
        else
        {
            _lpPitch = pitch;
            _lpYaw = yaw;
        }

        _rateX = st.InvertGyroX ? yaw : -yaw;
        _rateY = st.InvertGyroY ? -pitch : pitch;

        bool held = st.Activation switch
        {
            GyroActivation.Always => true,
            GyroActivation.HoldL2 => s.L2Btn || s.L2 > 30,
            GyroActivation.HoldR2 => s.R2Btn || s.R2 > 30,
            GyroActivation.HoldL1 => s.L1,
            GyroActivation.HoldR1 => s.R1,
            GyroActivation.TouchpadTouch => s.Touching,
            _ => false,
        };
        GyroActive = st.GyroEnabled && held;
        if (!GyroActive)
        {
            _precisionPrimed = false;
            return (0, 0);
        }

        // Turn pad left -> look left, tilt nose up -> look up; per-axis scale applies before the curve,
        // so it acts like turning that axis slower or faster.
        double x = _rateX * st.GyroScaleX;
        double y = _rateY * st.GyroScaleY;

        return st.GyroCurve == GyroCurve.Linear ? LinearCurve(x, y) : ShapedCurve(x, y, dt, CurveProfile.For(st.GyroCurve));
    }

    /// <summary>Plain mapping: cut-off deadzone, constant sensitivity.</summary>
    (double x, double y) LinearCurve(double x, double y)
    {
        var st = _settings;
        double m = Math.Sqrt(x * x + y * y);
        if (m <= st.GyroDeadzone) return (0, 0);
        double k = (m - st.GyroDeadzone) / m; // soft deadzone: no jump at the threshold
        x *= k; y *= k;

        double scale = st.GyroSensitivity / 100.0;
        x *= scale; y *= scale;

        if (st.GyroAntiDeadzone > 0)
        {
            m = Math.Sqrt(x * x + y * y);
            double outM = st.GyroAntiDeadzone + (1 - st.GyroAntiDeadzone) * Math.Min(m, 1);
            x = x / m * outM; y = y / m * outM;
        }
        return (Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1));
    }

    double _smX, _smY;
    bool _precisionPrimed;

    /// <summary>
    /// Instead of cutting off below the deadzone, small motions are scaled down smoothly (so slowing
    /// down never stops the aim dead), averaged to remove stutter, and gain rises with speed.
    /// The profile sets how much smoothing and how strong the acceleration is.
    /// </summary>
    (double x, double y) ShapedCurve(double x, double y, double dt, CurveProfile p)
    {
        var st = _settings;
        if (!_precisionPrimed)
        {
            _smX = x; _smY = y;
            _precisionPrimed = true;
        }

        // Tiered smoothing: small, slow motions use the averaged value, fast motions stay raw.
        double a = 1 - Math.Exp(-dt / p.SmoothTau);
        _smX += a * (x - _smX);
        _smY += a * (y - _smY);
        double speed = Math.Sqrt(x * x + y * y);
        double raw = Math.Clamp((speed - p.SmoothFull) / (p.SmoothNone - p.SmoothFull), 0, 1);
        x = raw * x + (1 - raw) * _smX;
        y = raw * y + (1 - raw) * _smY;

        // Tightening instead of a cut-off: below the deadzone output shrinks with speed squared,
        // which still silences sensor noise at rest but lets slow aiming continue.
        double m = Math.Sqrt(x * x + y * y);
        if (m == 0) return (0, 0);
        double dz = st.GyroDeadzone;
        double ramp = dz > 0 ? Math.Min(m / dz, 1) : 1;
        x *= ramp; y *= ramp;

        // Speed-dependent gain (smoothstep between the slow and fast speeds).
        double t = Math.Clamp((m - p.SlowSpeed) / (p.FastSpeed - p.SlowSpeed), 0, 1);
        t = t * t * (3 - 2 * t);
        double gain = p.SlowGain + (p.FastGain - p.SlowGain) * t;
        double scale = gain * st.GyroSensitivity / 100.0;
        x *= scale; y *= scale;

        // Anti-deadzone fades in with the ramp. Sensor noise on a still pad sits below the deadzone, where the
        // ramp is already partly open; with StopRestingCreep it only fades in between 1x and 2x the deadzone.
        if (st.GyroAntiDeadzone > 0)
        {
            double fade = !st.StopRestingCreep ? ramp
                : dz > 0 ? Math.Clamp((m - dz) / dz, 0, 1) : 1;
            double om = Math.Sqrt(x * x + y * y);
            if (om == 0) return (0, 0);
            double outM = st.GyroAntiDeadzone * fade + (1 - st.GyroAntiDeadzone) * Math.Min(om, 1);
            x = x / om * outM; y = y / om * outM;
        }
        return (Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1));
    }

    /// <summary>Estimated motor speed: spins up in ~80 ms, coasts down in ~250 ms.</summary>
    static double FollowMotor(double current, byte target, double dt)
    {
        double tau = target > current ? 0.08 : 0.25;
        return current + (target - current) * (1 - Math.Exp(-dt / tau));
    }

    double[]? RumbleTable() =>
        _settings.RumbleCompensation.TryGetValue(Kind.ToString(), out var t) && t.Length == 20 ? t
        : Kind == PadKind.DualSense ? DefaultDualSenseRumbleComp
        : null;

    /// <summary>Expected gyro offset (pitch, yaw in deg/s) for the current motor speeds.</summary>
    (double pitch, double yaw) RumbleOffset()
    {
        var t = RumbleTable();
        if (t == null) return (0, 0);
        return (Interp(t, 0, _effLarge, 0) + Interp(t, 1, _effSmall, 0),
                Interp(t, 0, _effLarge, 1) + Interp(t, 1, _effSmall, 1));
    }

    static double Interp(double[] t, int motor, double level, int axis)
    {
        level = Math.Clamp(level, 0, 255);
        int i = 0;
        while (i < RumbleLevelPoints.Length - 2 && level > RumbleLevelPoints[i + 1]) i++;
        double f = (level - RumbleLevelPoints[i]) / (RumbleLevelPoints[i + 1] - RumbleLevelPoints[i]);
        double a = t[motor * 10 + i * 2 + axis], b = t[motor * 10 + (i + 1) * 2 + axis];
        return a + (b - a) * f;
    }

    static (byte large, byte small) RumbleCalLevels(int step) => step switch
    {
        0 => ((byte)0, (byte)0),
        <= 4 => ((byte)Math.Min(255, step * 64), (byte)0),
        _ => ((byte)0, (byte)Math.Min(255, (step - 4) * 64)),
    };

    void StartRumbleCalStep(int step)
    {
        _rcStep = step;
        _rcStepStart = Environment.TickCount64;
        _rcSumPitch = _rcSumYaw = 0;
        _rcCount = 0;
        var (l, sm) = RumbleCalLevels(step);
        SendRumble(l, sm);
    }

    void RumbleCalibrationTick(ref PadState s)
    {
        Status = $"Calibrating rumble {_rcStep + 1}/{RumbleCalSteps} - keep still on a table";
        long elapsed = Environment.TickCount64 - _rcStepStart;
        if (elapsed < RumbleSettleMs) return;
        _rcSumPitch += s.GX / GyroCountsPerDps;
        _rcSumYaw += s.GY / GyroCountsPerDps;
        _rcCount++;
        if (elapsed < RumbleSettleMs + RumbleMeasureMs) return;

        _rcPitch[_rcStep] = _rcSumPitch / Math.Max(_rcCount, 1);
        _rcYaw[_rcStep] = _rcSumYaw / Math.Max(_rcCount, 1);
        if (_rcStep + 1 < RumbleCalSteps)
        {
            StartRumbleCalStep(_rcStep + 1);
            return;
        }

        // Offsets relative to the motors-off baseline; level 0 is zero by definition.
        var table = new double[20];
        for (int motor = 0; motor < 2; motor++)
            for (int lvl = 1; lvl <= 4; lvl++)
            {
                int step = motor * 4 + lvl;
                table[motor * 10 + lvl * 2] = _rcPitch[step] - _rcPitch[0];
                table[motor * 10 + lvl * 2 + 1] = _rcYaw[step] - _rcYaw[0];
            }
        _settings.RumbleCompensation = new Dictionary<string, double[]>(_settings.RumbleCompensation)
        {
            [Kind.ToString()] = table,
        };
        _settings.Save();

        // The pad was still for the baseline, so it doubles as a gyro bias calibration.
        _biasX = _rcPitch[0] * GyroCountsPerDps;
        _biasY = _rcYaw[0] * GyroCountsPerDps;
        SendRumble(0, 0);
        _rumbleCalActive = false;
    }

    void SendRumble(byte large, byte small)
    {
        if (large == _lastLarge && small == _lastSmall) return;
        if (Kind == PadKind.Handheld)
        {
            if (_xSlot >= 0) XInput.SetRumble(_xSlot, large, small);
            _lastLarge = large;
            _lastSmall = small;
            return;
        }
        var r = new byte[_outLen];
        if (Kind == PadKind.DualShock4)
        {
            if (Bluetooth)
            {
                r[0] = 0x11;
                r[1] = 0xC4; // HID + CRC, 4 ms poll interval
                r[3] = 0x01; // motors only
                r[6] = small;
                r[7] = large;
            }
            else
            {
                r[0] = 0x05;
                r[1] = 0x01;
                r[4] = small;
                r[5] = large;
            }
        }
        else
        {
            int c; // start of the common output block
            if (Bluetooth)
            {
                r[0] = 0x31;
                r[1] = (byte)(_btSeq++ << 4);
                r[2] = 0x10;
                c = 3;
            }
            else
            {
                r[0] = 0x02;
                c = 1;
            }
            r[c] = 0x03; // compatible vibration + haptics select
            r[c + 2] = small; // right (weak) motor
            r[c + 3] = large; // left (strong) motor
        }
        if (Bluetooth)
        {
            uint crc = Crc32.Compute(0xA2, r, 74);
            r[74] = (byte)crc; r[75] = (byte)(crc >> 8); r[76] = (byte)(crc >> 16); r[77] = (byte)(crc >> 24);
        }

        lock (_writeLock)
        {
            try
            {
                _stream!.Write(r);
                _lastLarge = large;
                _lastSmall = small;
            }
            catch (Exception ex) { Debug.WriteLine("Rumble write failed: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _stream?.Dispose(); } catch { }
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(1500);
        DisconnectVirtual();
    }
}

static class Crc32
{
    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>CRC32 of a one-byte Bluetooth header followed by data[0..len).</summary>
    public static uint Compute(byte header, byte[] data, int len)
    {
        uint crc = 0xFFFFFFFF;
        crc = Table[(crc ^ header) & 0xFF] ^ (crc >> 8);
        for (int i = 0; i < len; i++) crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
