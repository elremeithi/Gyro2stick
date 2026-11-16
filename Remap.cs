using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DSGyro360;

/// <summary>
/// One controller button's job in a remap preset. <see cref="Output"/> is a key combination ("Space", "Ctrl+S"),
/// "Mouse:Left|Right|Middle" or "Wheel:Up|Down".
/// </summary>
/// <param name="Hold">Tap / hold split: a quick press sends <see cref="Output"/>, holding past
/// <see cref="RemapEngine.HoldMs"/> sends <see cref="HoldOutput"/> instead.</param>
/// <param name="With">Only while this other button is held (a second layer). The other button's own action then
/// runs on release, and only if it wasn't used for a combination.</param>
/// <param name="Toggle">Each press latches the key down or lets it go (for games that only have "hold to crouch").</param>
/// <param name="ReleaseWhenStill">With Toggle: also let go when the left stick returns to the centre (console-style sprint).</param>
public sealed record RemapBinding(
    MacroButton Button, string Action, string Output,
    string? HoldAction = null, string? HoldOutput = null,
    MacroButton? With = null, bool Toggle = false, bool Tap = false, bool ReleaseWhenStill = false);

/// <summary>A game's controller layout for a keyboard-and-mouse-only game.</summary>
public sealed class RemapPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Process names (without .exe) and window-title text that count as "the game is in front".</summary>
    public required string[] Processes { get; init; }
    public required string TitleContains { get; init; }
    public required string Notes { get; init; }

    public string Forward { get; init; } = "W";
    public string Back { get; init; } = "S";
    public string Left { get; init; } = "A";
    public string Right { get; init; } = "D";

    /// <summary>Key held by the stick's tilt: a "run" key held at full tilt, or a "sneak" key held at a gentle tilt.</summary>
    public string? SpeedKey { get; init; }
    public string SpeedAction { get; init; } = "";
    public bool SpeedKeyWhenGentle { get; init; }
    public double SpeedThreshold { get; init; } = 0.8;

    public required RemapBinding[] Bindings { get; init; }

    /// <summary>Rows for the layout table: (controller input, game action, key).</summary>
    public IEnumerable<(string Input, string Action, string Key)> Rows()
    {
        yield return ("Left stick", "Move", $"{Forward} {Left} {Back} {Right}");
        if (SpeedKey != null)
            yield return (SpeedKeyWhenGentle ? "Left stick, gentle tilt" : "Left stick, full tilt", SpeedAction, Pretty(SpeedKey));
        yield return ("Right stick", "Look", "Mouse");
        yield return ("Gyro", "Look (fine aim)", "Mouse");
        foreach (var b in Bindings)
        {
            string input = (b.With is { } w ? MacroStep.ButtonName(w) + "  +  " : "") + MacroStep.ButtonName(b.Button);
            string action = b.Action + (b.ReleaseWhenStill ? " (click; stops when you stop)" : b.Toggle ? " (toggle)" : "");
            yield return (input + (b.HoldOutput != null ? "  (tap)" : ""), action, Pretty(b.Output));
            if (b.HoldOutput != null)
                yield return (MacroStep.ButtonName(b.Button) + "  (hold)", b.HoldAction ?? "", Pretty(b.HoldOutput));
        }
    }

    public static string Pretty(string output) => output switch
    {
        "Mouse:Left" => "Left mouse button",
        "Mouse:Right" => "Right mouse button",
        "Mouse:Middle" => "Middle mouse button",
        "Wheel:Up" => "Mouse wheel up",
        "Wheel:Down" => "Mouse wheel down",
        "OemCloseBrackets" => "]",
        "OemOpenBrackets" => "[",
        "Oemtilde" => "`",
        "LShiftKey" => "Left Shift",
        "LControlKey" => "Left Ctrl",
        "Back" => "Backspace",
        "CapsLock" => "Caps Lock",
        "Escape" => "Esc",
        _ => output,
    };
}

public static class RemapPresets
{
    /// <summary>
    /// The Operative: No One Lives Forever (2000). Keys are the game's defaults from the manual's "Default Key
    /// Configuration Chart": Fire = left mouse, Use = right mouse, Reload = middle mouse, next ammo / gadget function
    /// = Left Ctrl, next weapon = Left Shift, previous weapon = ], run = Enter, zoom = mouse wheel, objectives = O (hold).
    /// </summary>
    public static readonly RemapPreset Nolf1 = new()
    {
        Id = "nolf1",
        Name = "No One Lives Forever (2000)",
        Processes = new[] { "lithtech", "nolf" },
        TitleContains = "No One Lives Forever",
        Notes = "Uses the game's default keys: keep them, or click Restore Defaults in the game's Controls menu. " +
                "Turn Always Run off in the game's options: pushing the stick all the way runs, a gentle push walks quietly. " +
                "Gadgets are weapons in this game: pick one with LB / RB, then use it with RT. Hold Menu / Options and press D-pad up to quick save, down to quick load.",
        SpeedKey = "Enter",
        SpeedAction = "Run (walk when pushed gently)",
        SpeedKeyWhenGentle = false,
        SpeedThreshold = 0.85,
        Bindings = new RemapBinding[]
        {
            new(MacroButton.RT, "Fire / use gadget", "Mouse:Left"),
            new(MacroButton.LT, "Zoom in (scopes, sunglasses camera)", "Wheel:Up", Tap: true),
            new(MacroButton.RS, "Zoom out", "Wheel:Down", Tap: true),
            new(MacroButton.A, "Jump", "Space"),
            new(MacroButton.B, "Crouch", "C", Toggle: true),
            new(MacroButton.X, "Reload", "Mouse:Middle"),
            new(MacroButton.Y, "Use / activate (doors, people, pick up)", "Mouse:Right"),
            new(MacroButton.RB, "Next weapon / gadget", "LShiftKey"),
            new(MacroButton.LB, "Previous weapon / gadget", "OemCloseBrackets"),
            new(MacroButton.Up, "Next ammo type / gadget mode", "LControlKey"),
            new(MacroButton.Down, "Holster / draw weapon", "H"),
            new(MacroButton.Left, "Torch", "L"),
            new(MacroButton.Right, "Inventory", "I"),
            new(MacroButton.LS, "Always run on / off", "CapsLock"),
            new(MacroButton.Back, "Mission objectives (while held)", "O"),
            new(MacroButton.Start, "Menu / pause", "Escape"),
            new(MacroButton.Up, "Quick save", "F6", With: MacroButton.Start),
            new(MacroButton.Down, "Quick load", "F9", With: MacroButton.Start),
            new(MacroButton.Left, "Status screen", "Tab", With: MacroButton.Start),
            new(MacroButton.Right, "Turn around", "Back", With: MacroButton.Start),
            new(MacroButton.X, "Crosshair on / off", "X", With: MacroButton.Start),
            new(MacroButton.Y, "Centre view", "End", With: MacroButton.Start),
        },
    };

    /// <summary>
    /// No One Lives Forever 2: A Spy in H.A.R.M.'s Way (2002). Keys are the defaults from the manual's chart:
    /// Fire = left mouse, Action = Enter / right mouse, Reload = R, Change Function = F, next / previous weapon = [ / ],
    /// Holster = H, Last Weapon = X, Move Body = G, Keychain Light = L, Zoom = Z, Lean = Q / E, Sneak = Left Shift (held),
    /// Toggle Crouch = Left Ctrl, Mission Status = Tab, Player Status = P, Intelligence = `.
    /// </summary>
    public static readonly RemapPreset Nolf2 = new()
    {
        Id = "nolf2",
        Name = "No One Lives Forever 2 (2002)",
        Processes = new[] { "nolf2", "lithtech" },
        TitleContains = "No One Lives Forever",
        Notes = "Uses the game's default keys: keep them, or click Restore defaults in the game's Controls menu. " +
                "Leave Always Run on (the default): push the stick gently to sneak, all the way to run. " +
                "Lean with the D-pad while hiding behind cover. Tap Y to search or use, hold it to pick up or drop a body. Hold Menu / Options and press D-pad up to quick save, down to quick load.",
        SpeedKey = "LShiftKey",
        SpeedAction = "Sneak (quiet walk)",
        SpeedKeyWhenGentle = true,
        SpeedThreshold = 0.6,
        Bindings = new RemapBinding[]
        {
            new(MacroButton.RT, "Fire / use gadget", "Mouse:Left"),
            new(MacroButton.LT, "Zoom (scopes, camera)", "Z", Tap: true),
            new(MacroButton.A, "Jump", "Space"),
            new(MacroButton.B, "Crouch", "LControlKey", Tap: true),
            new(MacroButton.X, "Reload", "R"),
            new(MacroButton.Y, "Action: use, open, search, talk", "Enter", "Pick up / drop body", "G"),
            new(MacroButton.RB, "Next weapon / gadget", "OemOpenBrackets"),
            new(MacroButton.LB, "Previous weapon / gadget", "OemCloseBrackets"),
            new(MacroButton.Up, "Change function / ammo type", "F"),
            new(MacroButton.Down, "Holster / draw weapon", "H"),
            new(MacroButton.Left, "Lean left (while held)", "Q"),
            new(MacroButton.Right, "Lean right (while held)", "E"),
            new(MacroButton.LS, "Keychain light", "L"),
            new(MacroButton.RS, "Last weapon", "X"),
            new(MacroButton.Back, "Mission status", "Tab"),
            new(MacroButton.Start, "Menu / pause", "Escape"),
            new(MacroButton.Up, "Quick save", "F6", With: MacroButton.Start),
            new(MacroButton.Down, "Quick load", "F9", With: MacroButton.Start),
            new(MacroButton.Left, "Player status (skills)", "P", With: MacroButton.Start),
            new(MacroButton.Right, "Intelligence", "Oemtilde", With: MacroButton.Start),
            new(MacroButton.Y, "Turn around", "Back", With: MacroButton.Start),
            new(MacroButton.X, "Centre view", "End", With: MacroButton.Start),
        },
    };

    /// <summary>
    /// Battlefield: Bad Company 2 (2010), PC. Keys are the PC defaults; the layout follows the game's own Xbox 360 /
    /// PS3 layout (StrategyWiki controls page): Fire = left mouse, scope = right mouse, melee = middle mouse, grenade = F,
    /// gadgets = 3 / 4, interact / enter / exit = E, sprint = Shift, crouch = Ctrl, communicate / spot = Q,
    /// scoreboard = Tab, map = M, change camera = C, seats = 1-5, UAV up / down = R / F.
    /// </summary>
    public static readonly RemapPreset Bc2 = new()
    {
        Id = "bc2",
        Name = "Battlefield: Bad Company 2 (2010)",
        Processes = new[] { "BFBC2Game" },
        TitleContains = "Bad Company 2",
        Notes = "Uses the game's default PC keys (Options > Controls > Restore defaults). The buttons follow the game's own console layout. " +
                "Click L3 to sprint; sprinting stops when you let go of the stick. " +
                "Vehicles: left stick drives (W A S D), RT fires, LT is secondary fire, B gets out, D-pad up changes the camera. " +
                "Helicopters: left stick up / down climbs and descends, left / right turns, the right stick pitches and rolls. " +
                "Seats: Menu + D-pad left / right for seats 1 / 2, D-pad left / right alone for seats 3 / 4.",
        Bindings = new RemapBinding[]
        {
            new(MacroButton.RT, "Fire (vehicles: fire / horn)", "Mouse:Left"),
            new(MacroButton.LT, "Aim / scope, detonate C4 (vehicles: secondary fire)", "Mouse:Right"),
            new(MacroButton.A, "Jump (tap again in the air for the parachute)", "Space"),
            new(MacroButton.B, "Interact, enter / exit vehicle, pick up kit", "E"),
            new(MacroButton.X, "Reload (UAV: climb)", "R"),
            new(MacroButton.Y, "Switch weapon", "Wheel:Up", Tap: true),
            new(MacroButton.LB, "Throw grenade (UAV: descend)", "F"),
            new(MacroButton.RB, "Melee (knife)", "Mouse:Middle"),
            new(MacroButton.LS, "Sprint", "LShiftKey", Toggle: true, ReleaseWhenStill: true),
            new(MacroButton.RS, "Crouch", "LControlKey", Toggle: true),
            new(MacroButton.Left, "Primary gadget (vehicle: seat 3)", "3"),
            new(MacroButton.Right, "Secondary gadget (vehicle: seat 4)", "4"),
            new(MacroButton.Up, "Change camera (vehicles)", "C"),
            new(MacroButton.Down, "Map", "M"),
            new(MacroButton.Back, "Spot enemy / communicate", "Q", "Scoreboard (while held)", "Tab"),
            new(MacroButton.Start, "Menu", "Escape"),
            new(MacroButton.Left, "Sidearm (vehicle: seat 1, driver)", "1", With: MacroButton.Start),
            new(MacroButton.Right, "Primary weapon (vehicle: seat 2)", "2", With: MacroButton.Start),
            new(MacroButton.Up, "Binoculars / seat 5", "5", With: MacroButton.Start),
            new(MacroButton.A, "Respawn / squad menu", "Enter", With: MacroButton.Start),
        },
    };

    public static readonly RemapPreset[] All = { Nolf1, Nolf2, Bc2 };

    public static RemapPreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>What the remap engine reads from the controller each report.</summary>
struct RemapInput
{
    public bool[] Buttons;     // indexed by MacroButton
    public double LX, LY, RX, RY; // -1..1 after deadzone, up positive
    public double GyroX, GyroY;  // deg/s, right / up positive
    public bool GyroActive;
}

/// <summary>Turns controller input into keyboard keys, mouse buttons and mouse movement for one controller.</summary>
sealed class RemapEngine
{
    public const int HoldMs = 350;
    const int TapMs = 50;
    const double LookPixelsPerSecond = 1400;   // full right-stick tilt at 100 %
    const double GyroPixelsPerDegree = 14;     // at 100 %

    sealed class ButtonState
    {
        public bool Down;
        public long DownAt;
        public bool HoldFired, UsedAsModifier, Latched, Consumed;
    }

    RemapPreset? _preset;
    readonly ButtonState[] _buttons = Enumerable.Range(0, Enum.GetValues<MacroButton>().Length).Select(_ => new ButtonState()).ToArray();
    readonly Dictionary<string, int> _held = new();          // output -> how many sources hold it
    readonly List<(string Output, long UpAt)> _taps = new();
    readonly HashSet<string> _activeCombos = new();
    double _mouseX, _mouseY;
    long _lastTicks;

    /// <summary>Shown on the Remap page.</summary>
    public static volatile string Status = "";

    public void Process(RemapPreset preset, AppSettings st, in RemapInput input)
    {
        if (!ReferenceEquals(preset, _preset))
        {
            ReleaseAll();
            _preset = preset;
        }
        long now = Environment.TickCount64;
        long ticks = Stopwatch.GetTimestamp();
        double dt = _lastTicks == 0 ? 0.004 : Math.Clamp((ticks - _lastTicks) / (double)Stopwatch.Frequency, 0.0005, 0.05);
        _lastTicks = ticks;

        // Finished taps
        for (int i = _taps.Count - 1; i >= 0; i--)
            if (now >= _taps[i].UpAt)
            {
                Release(_taps[i].Output);
                _taps.RemoveAt(i);
            }

        Movement(preset, input);
        Look(st, input, dt);
        Buttons(preset, input, now);

        // Console-style sprint: stops when the stick comes back to the centre.
        if (Math.Sqrt(input.LX * input.LX + input.LY * input.LY) < 0.3)
            foreach (var b in preset.Bindings.Where(x => x.ReleaseWhenStill))
            {
                var s = _buttons[(int)b.Button];
                if (!s.Latched) continue;
                s.Latched = false;
                Release("toggle:" + b.Button);
            }
    }

    void Movement(RemapPreset p, in RemapInput input)
    {
        const double On = 0.35;
        double m = Math.Sqrt(input.LX * input.LX + input.LY * input.LY);
        // Direction keys from the stick angle, with overlap so diagonals press both keys.
        Set("stick:fwd", p.Forward, input.LY > On);
        Set("stick:back", p.Back, input.LY < -On);
        Set("stick:left", p.Left, input.LX < -On);
        Set("stick:right", p.Right, input.LX > On);
        if (p.SpeedKey != null)
        {
            bool moving = m > On;
            bool on = p.SpeedKeyWhenGentle ? moving && m < p.SpeedThreshold : m >= p.SpeedThreshold;
            Set("stick:speed", p.SpeedKey, on);
        }
    }

    void Look(AppSettings st, in RemapInput input, double dt)
    {
        // Right stick: squared response for fine control near the centre.
        double speed = LookPixelsPerSecond * st.RemapLookSpeed / 100.0;
        double rx = input.RX * Math.Abs(input.RX), ry = input.RY * Math.Abs(input.RY);
        _mouseX += rx * speed * dt;
        _mouseY -= ry * speed * dt;
        if (input.GyroActive)
        {
            double g = GyroPixelsPerDegree * st.RemapGyroSpeed / 100.0 * st.GyroSensitivity;
            _mouseX += input.GyroX * g * dt;
            _mouseY -= input.GyroY * g * dt;
        }
        int mx = (int)_mouseX, my = (int)_mouseY;
        if (mx != 0 || my != 0)
        {
            if (TestMouse != null) TestMouse(mx, my);
            else Mouse.Move(mx, my);
            _mouseX -= mx;
            _mouseY -= my;
        }
    }

    void Buttons(RemapPreset p, in RemapInput input, long now)
    {
        var bindings = p.Bindings;
        var modifiers = bindings.Where(b => b.With != null).Select(b => b.With!.Value).ToHashSet();

        // Edges
        foreach (var b in Enum.GetValues<MacroButton>())
        {
            var s = _buttons[(int)b];
            bool down = input.Buttons[(int)b];
            if (down && !s.Down)
            {
                s.Down = true;
                s.DownAt = now;
                s.HoldFired = s.UsedAsModifier = s.Consumed = false;
                OnPress(p, b, modifiers, now);
            }
            else if (!down && s.Down)
            {
                s.Down = false;
                OnRelease(p, b, modifiers, now);
            }
        }

        // Combinations held: release when either button goes up.
        foreach (var key in _activeCombos.ToList())
        {
            var parts = key.Split('|');
            var with = Enum.Parse<MacroButton>(parts[1]);
            var button = Enum.Parse<MacroButton>(parts[2]);
            if (!_buttons[(int)with].Down || !_buttons[(int)button].Down)
            {
                Release(key);
                _activeCombos.Remove(key);
            }
        }

        // Hold actions fire once the button has been held long enough.
        foreach (var bind in bindings.Where(x => x.HoldOutput != null && x.With == null))
        {
            var s = _buttons[(int)bind.Button];
            if (s.Down && !s.HoldFired && !s.Consumed && now - s.DownAt >= HoldMs)
            {
                s.HoldFired = true;
                Hold("hold:" + bind.Button, bind.HoldOutput!);
            }
        }
    }

    void OnPress(RemapPreset p, MacroButton b, HashSet<MacroButton> modifiers, long now)
    {
        // A combination with a held modifier wins over the button's own action.
        foreach (var combo in p.Bindings.Where(x => x.Button == b && x.With is { } w && _buttons[(int)w].Down))
        {
            var w = combo.With!.Value;
            _buttons[(int)w].UsedAsModifier = true;
            _buttons[(int)b].Consumed = true;
            string key = $"combo|{w}|{b}";
            if (_activeCombos.Add(key)) Hold(key, combo.Output);
            return;
        }
        var bind = p.Bindings.FirstOrDefault(x => x.Button == b && x.With == null);
        if (bind == null) return;
        if (modifiers.Contains(b) || bind.HoldOutput != null) return; // decided on release / after the hold time
        if (bind.Toggle)
        {
            var s = _buttons[(int)b];
            s.Latched = !s.Latched;
            Set("toggle:" + b, bind.Output, s.Latched);
            return;
        }
        if (bind.Tap)
        {
            TapOutput(bind.Output, now);
            return;
        }
        Hold("button:" + b, bind.Output);
    }

    void OnRelease(RemapPreset p, MacroButton b, HashSet<MacroButton> modifiers, long now)
    {
        var s = _buttons[(int)b];
        Release("button:" + b);
        if (s.HoldFired) Release("hold:" + b);
        if (s.Consumed) return;
        var bind = p.Bindings.FirstOrDefault(x => x.Button == b && x.With == null);
        if (bind == null || bind.Toggle) return;
        bool deferred = modifiers.Contains(b) || bind.HoldOutput != null;
        if (deferred && !s.UsedAsModifier && !s.HoldFired) TapOutput(bind.Output, now);
    }

    // ---------- outputs (reference counted by source) ----------

    readonly Dictionary<string, string> _sources = new(); // source -> output it holds

    void Set(string source, string output, bool on)
    {
        if (on) Hold(source, output);
        else Release(source);
    }

    void Hold(string source, string output)
    {
        if (_sources.ContainsKey(source)) return;
        _sources[source] = output;
        if (_held.TryGetValue(output, out int n) && n > 0) { _held[output] = n + 1; return; }
        _held[output] = 1;
        Send(output, down: true);
    }

    void Release(string source)
    {
        if (!_sources.Remove(source, out var output)) return;
        if (!_held.TryGetValue(output, out int n)) return;
        if (n > 1) { _held[output] = n - 1; return; }
        _held.Remove(output);
        Send(output, down: false);
    }

    void TapOutput(string output, long now)
    {
        string source = "tap:" + output + ":" + now;
        Hold(source, output);
        _taps.Add((source, now + TapMs));
    }

    /// <summary>Lets go of every key and mouse button this engine holds (remap turned off, game left, pad gone).</summary>
    public void ReleaseAll()
    {
        foreach (var source in _sources.Keys.ToList()) Release(source);
        _taps.Clear();
        _activeCombos.Clear();
        foreach (var s in _buttons) { s.Down = s.Latched = false; }
        _mouseX = _mouseY = 0;
        _lastTicks = 0;
    }

    /// <summary>Tests only: capture outputs instead of pressing real keys and moving the real mouse.</summary>
    public static Action<string, bool>? TestOutput;
    public static Action<int, int>? TestMouse;

    static void Send(string output, bool down)
    {
        if (TestOutput != null) { TestOutput(output, down); return; }
        switch (output)
        {
            case "Mouse:Left": MouseInput.Shortcut(ShortcutMouse.Left, down); return;
            case "Mouse:Right": MouseInput.Shortcut(ShortcutMouse.Right, down); return;
            case "Mouse:Middle": MouseInput.Shortcut(ShortcutMouse.Middle, down); return;
            case "Wheel:Up": if (down) MouseInput.Shortcut(ShortcutMouse.WheelUp, true); return;
            case "Wheel:Down": if (down) MouseInput.Shortcut(ShortcutMouse.WheelDown, true); return;
        }
        if (KeyCombo.Parse(output) is not { } combo) return;
        var keys = combo.Modifiers.Concat(combo.Key is { } k ? new[] { k } : Array.Empty<ushort>()).ToArray();
        if (down) foreach (var vk in keys) KeyInput.Down(vk);
        else foreach (var vk in keys.Reverse()) KeyInput.Up(vk);
    }

    static class Mouse
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MouseInputData { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct Input { public uint Type; public MouseInputData Mi; }

        [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);

        public static void Move(int dx, int dy)
        {
            var input = new[] { new Input { Type = 0, Mi = new MouseInputData { Dx = dx, Dy = dy, Flags = 0x0001 /* MOVE */ } } };
            SendInput(1, input, Marshal.SizeOf<Input>());
        }
    }
}

/// <summary>Which program is in front, checked at most twice a second.</summary>
static class Foreground
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    static long _checkedAt;
    static IntPtr _hwnd;
    static string _process = "", _title = "";
    static readonly object Lock = new();

    public static (string Process, string Title) Current()
    {
        lock (Lock)
        {
            long now = Environment.TickCount64;
            if (now - _checkedAt < 500) return (_process, _title);
            _checkedAt = now;
            var hwnd = GetForegroundWindow();
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            _title = sb.ToString();
            if (hwnd != _hwnd)
            {
                _hwnd = hwnd;
                _process = "";
                try
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    using var p = Process.GetProcessById((int)pid);
                    _process = p.ProcessName;
                }
                catch { /* gone or protected */ }
            }
            return (_process, _title);
        }
    }

    public static bool IsGame(RemapPreset p)
    {
        var (process, title) = Current();
        return p.Processes.Any(n => string.Equals(n, process, StringComparison.OrdinalIgnoreCase))
            || title.Contains(p.TitleContains, StringComparison.OrdinalIgnoreCase);
    }
}
