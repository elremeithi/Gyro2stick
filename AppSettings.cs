using System.Text.Json;

namespace DSGyro360;

public enum GyroActivation
{
    HoldL2,
    HoldR2,
    HoldL1,
    HoldR1,
    TouchpadTouch,
    Always,
}

public enum AppLayout { Landscape, Portrait }

public enum GyroCurve
{
    Linear,
    Balanced,
    Precision,
    Steady,
    Flick,
}

/// <summary>
/// Shape of a non-linear gyro curve. Speeds in deg/s; gain multiplies the sensitivity and rises smoothly
/// from SlowGain (at or below SlowSpeed) to FastGain (at or above FastSpeed). Motions slower than SmoothFull
/// are fully averaged over SmoothTau seconds, faster than SmoothNone are raw.
/// </summary>
public readonly record struct CurveProfile(
    double SmoothTau, double SmoothFull, double SmoothNone,
    double SlowSpeed, double FastSpeed, double SlowGain, double FastGain)
{
    // Close to 1:1 with the jitter cleaned up: everyday play, third-person games.
    static readonly CurveProfile Balanced = new(0.03, 1.0, 4, 5, 80, 0.85, 1.15);
    // Like Windows "Enhance pointer precision": fine slow aim, faster turns travel further.
    static readonly CurveProfile Precision = new(0.04, 1.5, 5, 4, 60, 0.6, 1.4);
    // Heavy smoothing and low slow-speed gain for holding still on distant targets (tactical / sniping).
    static readonly CurveProfile Steady = new(0.07, 2.5, 9, 3, 90, 0.45, 1.05);
    // Light smoothing and strong acceleration: quick wrist flicks turn far (fast arena shooters).
    static readonly CurveProfile Flick = new(0.02, 1.0, 3, 5, 50, 0.7, 2.0);

    public static CurveProfile For(GyroCurve curve) => curve switch
    {
        GyroCurve.Balanced => Balanced,
        GyroCurve.Steady => Steady,
        GyroCurve.Flick => Flick,
        _ => Precision,
    };
}

/// <summary>
/// User settings. Read directly by controller threads every frame, so keep it to plain fields.
/// </summary>
public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;

    public bool GyroEnabled { get; set; } = true;
    public GyroActivation Activation { get; set; } = GyroActivation.HoldL2;
    /// <summary>1.0 = 100 deg/s of rotation gives full right-stick deflection.</summary>
    public double GyroSensitivity { get; set; } = 1.5;
    /// <summary>Per-axis multipliers on the gyro turn rate (horizontal / vertical), 0.1..2.</summary>
    public double GyroScaleX { get; set; } = 1.0;
    public double GyroScaleY { get; set; } = 1.0;
    /// <summary>Rotation speed (deg/s) below which gyro is ignored.</summary>
    public double GyroDeadzone { get; set; } = 1.0;
    /// <summary>Minimum stick output (0..0.5) to push past the game's own deadzone.</summary>
    public double GyroAntiDeadzone { get; set; } = 0.0;
    public GyroCurve GyroCurve { get; set; } = GyroCurve.Linear;
    /// <summary>Non-linear curves: no anti-deadzone below the gyro deadzone, so a still pad doesn't creep.</summary>
    public bool StopRestingCreep { get; set; } = true;
    /// <summary>Old on/off setting, replaced by <see cref="GyroCurve"/>.Precision; only read when loading.</summary>
    public bool EnhancePrecision { get; set; }
    public bool InvertGyroX { get; set; }
    public bool InvertGyroY { get; set; }

    /// <summary>Radial deadzone for the physical sticks (0..0.5).</summary>
    public double StickDeadzone { get; set; } = 0.05;
    /// <summary>Minimum output (0..0.5) once a stick leaves its deadzone, to get past the game's own deadzone.</summary>
    public double LeftStickAntiDeadzone { get; set; }
    public double RightStickAntiDeadzone { get; set; }

    /// <summary>Handheld PCs (GPD Win 4): use the built-in controller with the built-in gyro.</summary>
    public BuiltInMode BuiltInController { get; set; } = BuiltInMode.Auto;
    /// <summary>Sensor axis for turning left/right (left = positive) and for tilting to look up (up = positive).</summary>
    public SensorAxis BuiltInTurnAxis { get; set; } = SensorAxis.PlusY;
    public SensorAxis BuiltInTiltAxis { get; set; } = SensorAxis.PlusX;

    public bool HideControllers { get; set; } = true;
    public bool Rumble { get; set; } = true;
    /// <summary>Scales game rumble (0..1).</summary>
    public double RumbleStrength { get; set; } = 1.0;
    /// <summary>Cancel the gyro drift/shake that the rumble motors cause.</summary>
    public bool RumbleGyroFix { get; set; } = true;
    /// <summary>
    /// Measured gyro offset caused by rumble, per controller type: [motor 0=large,1=small][level 0,64,128,192,255][axis 0=pitch,1=yaw]
    /// in deg/s, flattened (20 values). Replaced (never mutated) so saving can't race the controller threads.
    /// </summary>
    public Dictionary<string, double[]> RumbleCompensation { get; set; } = new();

    /// <summary>PS + button combos that take a screenshot / start-stop recording without the game seeing them.</summary>
    public CaptureTool CaptureTool { get; set; } = CaptureTool.GameBar;
    public ChordButton ScreenshotButton { get; set; } = ChordButton.Create;
    public ChordButton RecordButton { get; set; } = ChordButton.Options;
    /// <summary>PS + button shortcuts (keys or programs). Replaced on Apply, never mutated in the live settings.</summary>
    public List<Shortcut> Shortcuts { get; set; } = new() { new Shortcut() };
    /// <summary>Button-combo macros. Replaced on Apply, never mutated in the live settings.</summary>
    public List<Macro> Macros { get; set; } = new();

    /// <summary>Remap preset id ("" = off: the controller stays an Xbox controller).</summary>
    public string RemapPreset { get; set; } = "";
    /// <summary>Only remap while the preset's game is the window in front (otherwise anywhere).</summary>
    public bool RemapOnlyInGame { get; set; } = true;
    /// <summary>Right-stick look speed, percent.</summary>
    public int RemapLookSpeed { get; set; } = 100;
    /// <summary>Gyro look speed, percent.</summary>
    public int RemapGyroSpeed { get; set; } = 100;
    /// <summary>Gyro moves the mouse all the time while remapped, instead of only while the Settings-page activation is held.</summary>
    public bool RemapGyroAlways { get; set; }

    /// <summary>Window layout; switched instantly from the header, not through Apply.</summary>
    public AppLayout Layout { get; set; } = AppLayout.Landscape;

    // Performance page (applied instantly, not through Apply).
    public PerfProfile PerfProfile { get; set; } = PerfProfile.Off;
    /// <summary>Power limit in watts for each frame-rate profile.</summary>
    public int TdpFps30 { get; set; } = 12;
    public int TdpFps45 { get; set; } = 18;
    public int TdpFps60 { get; set; } = 28;
    /// <summary>Also cap the frame rate with RivaTuner Statistics Server.</summary>
    public bool CapFps { get; set; } = true;
    /// <summary>True while RTSS holds a cap we set, so only our own cap is ever removed.</summary>
    public bool RtssCapSet { get; set; }
    public FanMode FanMode { get; set; } = FanMode.Curve;
    public int FanFixedPercent { get; set; } = 50;

    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;

    /// <summary>Per-game profiles: each holds a copy of the options above (see <see cref="CopyProfileFrom"/>).</summary>
    public List<GameProfile> Profiles { get; set; } = new();
    public string ActiveProfile { get; set; } = GameProfile.DefaultName;
    /// <summary>Switch to a game's profile while that game is the window in front.</summary>
    public bool AutoSwitchProfiles { get; set; } = true;

    /// <summary>Device instance IDs we told HidHide to hide, so they can be restored after a crash.</summary>
    public List<string> HiddenInstanceIds { get; set; } = new();

    /// <summary>USB audio functions of hidden pads that we disabled, so they can be re-enabled after a crash.</summary>
    public List<string> DisabledAudioIds { get; set; } = new();

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSGyro360");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    readonly object _saveLock = new();

    /// <summary>Copies the user-editable options (not the ON/OFF switch or HidHide bookkeeping).</summary>
    public void CopyOptionsFrom(AppSettings o)
    {
        GyroEnabled = o.GyroEnabled;
        Activation = o.Activation;
        GyroSensitivity = o.GyroSensitivity;
        GyroScaleX = o.GyroScaleX;
        GyroScaleY = o.GyroScaleY;
        GyroDeadzone = o.GyroDeadzone;
        GyroAntiDeadzone = o.GyroAntiDeadzone;
        GyroCurve = o.GyroCurve;
        StopRestingCreep = o.StopRestingCreep;
        InvertGyroX = o.InvertGyroX;
        InvertGyroY = o.InvertGyroY;
        StickDeadzone = o.StickDeadzone;
        LeftStickAntiDeadzone = o.LeftStickAntiDeadzone;
        RightStickAntiDeadzone = o.RightStickAntiDeadzone;
        BuiltInController = o.BuiltInController;
        BuiltInTurnAxis = o.BuiltInTurnAxis;
        BuiltInTiltAxis = o.BuiltInTiltAxis;
        HideControllers = o.HideControllers;
        Rumble = o.Rumble;
        RumbleStrength = o.RumbleStrength;
        RumbleGyroFix = o.RumbleGyroFix;
        CaptureTool = o.CaptureTool;
        ScreenshotButton = o.ScreenshotButton;
        RecordButton = o.RecordButton;
        Shortcuts = o.Shortcuts.Select(s => s.Clone()).ToList();
        Macros = o.Macros.Select(m => m.Clone()).ToList();
        RemapPreset = o.RemapPreset;
        RemapOnlyInGame = o.RemapOnlyInGame;
        RemapLookSpeed = o.RemapLookSpeed;
        RemapGyroSpeed = o.RemapGyroSpeed;
        RemapGyroAlways = o.RemapGyroAlways;
        StartWithWindows = o.StartWithWindows;
        StartMinimized = o.StartMinimized;
        CloseToTray = o.CloseToTray;
    }

    /// <summary>
    /// Copies what a profile holds: everything in <see cref="CopyOptionsFrom"/> plus the performance page, but not
    /// app-wide or device settings (start with Windows, tray, built-in controller axes).
    /// </summary>
    public void CopyProfileFrom(AppSettings o)
    {
        var (sww, min, tray, builtIn, turn, tilt) =
            (StartWithWindows, StartMinimized, CloseToTray, BuiltInController, BuiltInTurnAxis, BuiltInTiltAxis);
        CopyOptionsFrom(o);
        (StartWithWindows, StartMinimized, CloseToTray, BuiltInController, BuiltInTurnAxis, BuiltInTiltAxis) =
            (sww, min, tray, builtIn, turn, tilt);
        PerfProfile = o.PerfProfile;
        TdpFps30 = o.TdpFps30;
        TdpFps45 = o.TdpFps45;
        TdpFps60 = o.TdpFps60;
        CapFps = o.CapFps;
        FanMode = o.FanMode;
        FanFixedPercent = o.FanFixedPercent;
    }

    /// <summary>A copy of the current profile options, for storing in a profile.</summary>
    public AppSettings SnapshotProfile()
    {
        var s = new AppSettings();
        s.CopyProfileFrom(this);
        return s;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new();
                if (s.EnhancePrecision)
                {
                    s.GyroCurve = GyroCurve.Precision;
                    s.EnhancePrecision = false;
                }
                return s;
            }
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new();
    }

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
            }
            catch { }
        }
    }
}
