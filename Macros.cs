using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gyro2stick;

// =====================================================================================================
// Macros (modelled on reWASD's combos): a controller button combination + an activator starts a sequence of
// steps - gamepad buttons, keyboard keys, mouse clicks, waits, stick moves and rumble - that runs once,
// N times, while the combo is held, or on/off as a toggle.
// =====================================================================================================

/// <summary>When the combo starts the macro (reWASD's "activators").</summary>
public enum MacroTrigger { Press, LongPress, DoublePress, Release }

/// <summary>How often it runs: once (or N times), repeating while held, or repeating until pressed again.</summary>
public enum MacroRepeat { Once, WhileHeld, Toggle }

/// <summary>Buttons of the virtual Xbox 360 pad a macro can press.</summary>
public enum MacroButton { A, B, X, Y, LB, RB, LT, RT, Back, Start, LS, RS, Guide, Up, Down, Left, Right }

public enum MacroStepKind { Button, ButtonDown, ButtonUp, Key, KeyDown, KeyUp, Mouse, Wait, Stick, Rumble }

public enum MacroMouse { Left, Right, Middle }

public enum MacroStick { Left, Right }

public sealed class MacroStep
{
    public MacroStepKind Kind { get; set; } = MacroStepKind.Button;
    public MacroButton Button { get; set; } = MacroButton.A;
    /// <summary>Key combination for key steps, e.g. "Ctrl+C" or "Space".</summary>
    public string Keys { get; set; } = "Space";
    public MacroMouse Mouse { get; set; } = MacroMouse.Left;
    /// <summary>How long: the press for button/key/mouse taps, the pause for Wait, the hold for Stick/Rumble.</summary>
    public int Ms { get; set; } = 60;
    public MacroStick Stick { get; set; } = MacroStick.Left;
    /// <summary>Stick deflection -100..100 (up / right positive).</summary>
    public int X { get; set; }
    public int Y { get; set; } = 100;
    /// <summary>Rumble strength 0..100 %.</summary>
    public int Strength { get; set; } = 70;

    public MacroStep Clone() => (MacroStep)MemberwiseClone();

    public static string ButtonName(MacroButton b) => b switch
    {
        MacroButton.A => "A (Cross)",
        MacroButton.B => "B (Circle)",
        MacroButton.X => "X (Square)",
        MacroButton.Y => "Y (Triangle)",
        MacroButton.LB => "LB (L1)",
        MacroButton.RB => "RB (R1)",
        MacroButton.LT => "LT (L2)",
        MacroButton.RT => "RT (R2)",
        MacroButton.Back => "Back (Create)",
        MacroButton.Start => "Start (Options)",
        MacroButton.LS => "LS (L3)",
        MacroButton.RS => "RS (R3)",
        MacroButton.Guide => "Guide (PS)",
        MacroButton.Up => "D-pad up",
        MacroButton.Down => "D-pad down",
        MacroButton.Left => "D-pad left",
        _ => "D-pad right",
    };

    /// <summary>Title shown on the flow-chart block.</summary>
    public string Title => Kind switch
    {
        MacroStepKind.Button => "Press " + ButtonName(Button),
        MacroStepKind.ButtonDown => "Hold down " + ButtonName(Button),
        MacroStepKind.ButtonUp => "Release " + ButtonName(Button),
        MacroStepKind.Key => "Key " + Keys,
        MacroStepKind.KeyDown => "Key down " + Keys,
        MacroStepKind.KeyUp => "Key up " + Keys,
        MacroStepKind.Mouse => Mouse + " mouse click",
        MacroStepKind.Wait => "Wait",
        MacroStepKind.Stick => $"{Stick} stick  X {X}  Y {Y}",
        _ => $"Rumble {Strength}%",
    };

    /// <summary>Second line on the block (timing), or "" for instant steps.</summary>
    public string Detail => Kind switch
    {
        MacroStepKind.Button or MacroStepKind.Key or MacroStepKind.Mouse => $"held {Ms} ms",
        MacroStepKind.Wait => $"{Ms} ms",
        MacroStepKind.Stick or MacroStepKind.Rumble => $"for {Ms} ms",
        _ => "",
    };

    public bool Timed => Kind is MacroStepKind.Button or MacroStepKind.Key or MacroStepKind.Mouse
        or MacroStepKind.Wait or MacroStepKind.Stick or MacroStepKind.Rumble;
}

public sealed class Macro
{
    public string Name { get; set; } = "New macro";
    public bool Enabled { get; set; } = true;
    /// <summary>Controller buttons that must all be held (1-3).</summary>
    public List<ChordButton> Combo { get; set; } = new() { ChordButton.L1, ChordButton.Cross };
    public MacroTrigger Trigger { get; set; } = MacroTrigger.Press;
    /// <summary>Long-press time / double-press window in ms (reWASD default 300).</summary>
    public int TriggerMs { get; set; } = 300;
    public MacroRepeat Repeat { get; set; } = MacroRepeat.Once;
    /// <summary>Times to run for <see cref="MacroRepeat.Once"/>.</summary>
    public int Iterations { get; set; } = 1;
    public int PauseBetweenMs { get; set; }
    /// <summary>The game doesn't see the combo buttons while they trigger this macro.</summary>
    public bool BlockSource { get; set; } = true;
    public List<MacroStep> Steps { get; set; } = new();

    public Macro Clone()
    {
        var m = (Macro)MemberwiseClone();
        m.Combo = Combo.ToList();
        m.Steps = Steps.Select(s => s.Clone()).ToList();
        return m;
    }

    public static string ComboName(ChordButton b) => b switch
    {
        ChordButton.Create => "Create",
        ChordButton.TouchpadClick => "Touchpad",
        ChordButton.DpadUp => "Up",
        ChordButton.DpadDown => "Down",
        ChordButton.DpadLeft => "Left",
        ChordButton.DpadRight => "Right",
        _ => b.ToString(),
    };

    public string ComboText => Combo.Count == 0 ? "(no buttons)" : string.Join(" + ", Combo.Select(ComboName));

    public string TriggerText => Trigger switch
    {
        MacroTrigger.LongPress => $"held {TriggerMs} ms",
        MacroTrigger.DoublePress => "double-pressed",
        MacroTrigger.Release => "released",
        _ => "pressed",
    };

    public string RepeatText => Repeat switch
    {
        MacroRepeat.WhileHeld => "Repeat while held",
        MacroRepeat.Toggle => "Repeat until pressed again",
        _ => Iterations > 1 ? $"Run {Iterations} times" : "End",
    };

    /// <summary>Total time of one run, in ms.</summary>
    public int DurationMs => Steps.Where(s => s.Timed).Sum(s => s.Ms);

    /// <summary>Starter macro shown when one is created: hold LB + Cross to press A twice.</summary>
    /// <summary>Plain-language summary: "When you press L1 + Cross: press A, wait 80 ms, press A. Runs once."</summary>
    public string Describe()
    {
        string when = Trigger switch
        {
            MacroTrigger.LongPress => $"hold {ComboText} for {TriggerMs / 1000.0:0.##} s",
            MacroTrigger.DoublePress => $"double-press {ComboText}",
            MacroTrigger.Release => $"let go of {ComboText}",
            _ => $"press {ComboText}",
        };
        string what = Steps.Count == 0
            ? "nothing yet (add steps)"
            : string.Join(", then ", Steps.Select(s => s.Detail.Length > 0 ? $"{s.Title} ({s.Detail})" : s.Title).Take(4)) +
              (Steps.Count > 4 ? $", and {Steps.Count - 4} more" : "");
        string how = Repeat switch
        {
            MacroRepeat.WhileHeld => "It repeats for as long as you keep the buttons held.",
            MacroRepeat.Toggle => "It keeps repeating until you press the buttons again.",
            _ => Iterations > 1 ? $"It runs {Iterations} times." : "It runs once.",
        };
        return $"When you {when}, it does: {what}. {how}" + (Enabled ? "" : " (Turned off.)");
    }

    /// <summary>Ready-made macros for the New menu: (title, what it does, the macro).</summary>
    public static (string Title, string Tip, Func<Macro> Make)[] Templates =>
    [
        ("Blank macro", "Start from scratch: pick the buttons, then add steps.",
            () => new Macro { Name = "My macro", Steps = { } }),
        ("Turbo (rapid fire)", "Hold R1 and A is pressed over and over, about 12 times a second.",
            () => new Macro
            {
                Name = "Turbo A", Combo = new() { ChordButton.R1 }, Repeat = MacroRepeat.WhileHeld,
                Steps = { new() { Kind = MacroStepKind.Button, Button = MacroButton.A, Ms = 40 }, new() { Kind = MacroStepKind.Wait, Ms = 40 } },
            }),
        ("Press a button twice", "Press L1 + Cross and A is pressed twice, quickly.",
            () => WithName(Sample(), "Double A")),
        ("Toggle auto-run", "Click the left stick (L3) and the left stick stays pushed forward. Click it again to stop.",
            () => new Macro
            {
                Name = "Auto-run", Combo = new() { ChordButton.L3 }, Repeat = MacroRepeat.Toggle,
                Steps = { new() { Kind = MacroStepKind.Stick, Stick = MacroStick.Left, X = 0, Y = 100, Ms = 100 } },
            }),
        ("Toggle hold (aim)", "Tap L2 once and it stays held. Tap it again to let go.",
            () => new Macro
            {
                Name = "Hold aim", Combo = new() { ChordButton.L2 }, Repeat = MacroRepeat.Toggle,
                Steps = { new() { Kind = MacroStepKind.ButtonDown, Button = MacroButton.LT }, new() { Kind = MacroStepKind.Wait, Ms = 50 } },
            }),
        ("Combo move", "Press L1 + Circle to do Down, Right, then X, like a fighting-game special move.",
            () => new Macro
            {
                Name = "Special move", Combo = new() { ChordButton.L1, ChordButton.Circle },
                Steps =
                {
                    new() { Kind = MacroStepKind.Button, Button = MacroButton.Down, Ms = 50 },
                    new() { Kind = MacroStepKind.Wait, Ms = 30 },
                    new() { Kind = MacroStepKind.Button, Button = MacroButton.Right, Ms = 50 },
                    new() { Kind = MacroStepKind.Wait, Ms = 30 },
                    new() { Kind = MacroStepKind.Button, Button = MacroButton.X, Ms = 60 },
                },
            }),
        ("Keyboard shortcut", "Hold Options for half a second to press Alt+Tab on the keyboard.",
            () => new Macro
            {
                Name = "Switch window", Combo = new() { ChordButton.Options }, Trigger = MacroTrigger.LongPress, TriggerMs = 500,
                Steps = { new() { Kind = MacroStepKind.Key, Keys = "Alt+Tab", Ms = 60 } },
            }),
    ];

    static Macro WithName(Macro m, string name)
    {
        m.Name = name;
        return m;
    }

    public static Macro Sample() => new()
    {
        Steps =
        {
            new MacroStep { Kind = MacroStepKind.Button, Button = MacroButton.A, Ms = 60 },
            new MacroStep { Kind = MacroStepKind.Wait, Ms = 80 },
            new MacroStep { Kind = MacroStepKind.Button, Button = MacroButton.A, Ms = 60 },
        },
    };
}

/// <summary>Runs the macros for one controller. Called on its input thread every report.</summary>
sealed class MacroEngine
{
    sealed class Slot
    {
        public bool Down, LongFired, SinglePending;
        public long DownAt, FirstPressAt, TapUntil;
        public Run? Run;
    }

    sealed class Run
    {
        public required Macro Macro;
        public int Step, Iteration;
        public bool Started, Stopping;
        public long StepEnd;
        public readonly HashSet<MacroButton> Held = new();
        public readonly List<ushort> KeysDown = new();
        public readonly List<MacroMouse> MouseDown = new();
        public (MacroStick Side, int X, int Y)? Stick;
        public bool Rumbling;
    }

    List<Macro>? _macros;
    Slot[] _slots = Array.Empty<Slot>();
    readonly HashSet<ChordButton> _swallowed = new();
    readonly Action<byte, byte> _rumble;

    public MacroEngine(Action<byte, byte> rumble) => _rumble = rumble;

    /// <summary>True while any macro is running (rumble from the game is held back then).</summary>
    public bool Busy => _slots.Any(s => s.Run?.Rumbling == true);

    /// <summary>
    /// Reads the combos from <paramref name="pressed"/>, hides blocked combo buttons via <paramref name="release"/>,
    /// injects passthrough taps via <paramref name="press"/>, and returns what running macros want on the pad.
    /// </summary>
    public (HashSet<MacroButton> Buttons, List<(MacroStick Side, int X, int Y)> Sticks) Process(
        List<Macro> macros, Func<ChordButton, bool> pressed, Action<ChordButton> release, Action<ChordButton> press)
    {
        long now = Environment.TickCount64;
        if (!ReferenceEquals(macros, _macros))
        {
            StopAll();
            _macros = macros;
            _slots = macros.Select(_ => new Slot()).ToArray();
            _swallowed.Clear();
        }

        var block = new HashSet<ChordButton>();
        var inject = new HashSet<ChordButton>();
        for (int i = 0; i < macros.Count; i++)
        {
            var m = macros[i];
            var slot = _slots[i];
            if (!m.Enabled || m.Combo.Count == 0)
            {
                if (slot.Run != null) Stop(slot.Run, now);
                continue;
            }
            bool down = m.Combo.All(pressed);
            bool rising = down && !slot.Down, falling = !down && slot.Down;
            slot.Down = down;

            switch (m.Trigger)
            {
                case MacroTrigger.Press:
                    if (rising) Fire(m, slot, now);
                    break;
                case MacroTrigger.Release:
                    if (falling) Fire(m, slot, now);
                    break;
                case MacroTrigger.LongPress:
                    if (rising)
                    {
                        slot.DownAt = now;
                        slot.LongFired = false;
                    }
                    if (down && !slot.LongFired && now - slot.DownAt >= m.TriggerMs)
                    {
                        slot.LongFired = true;
                        Fire(m, slot, now);
                    }
                    if (falling && !slot.LongFired && m.BlockSource) slot.TapUntil = now + 60; // short press: let it through
                    break;
                case MacroTrigger.DoublePress:
                    if (rising)
                    {
                        if (slot.SinglePending && now - slot.FirstPressAt <= m.TriggerMs)
                        {
                            slot.SinglePending = false;
                            Fire(m, slot, now);
                        }
                        else
                        {
                            slot.SinglePending = true;
                            slot.FirstPressAt = now;
                        }
                    }
                    if (slot.SinglePending && !down && now - slot.FirstPressAt > m.TriggerMs)
                    {
                        slot.SinglePending = false; // only one press: give the game its press
                        if (m.BlockSource) slot.TapUntil = now + 60;
                    }
                    break;
            }

            // While held, a "repeat while held" macro keeps going; letting go stops it.
            if (falling && slot.Run != null && m.Repeat == MacroRepeat.WhileHeld && m.Trigger != MacroTrigger.Release)
                slot.Run.Stopping = true;

            if (m.BlockSource)
            {
                if (down)
                    foreach (var b in m.Combo) _swallowed.Add(b);
                // Long / double press can't know yet whether it's the macro: hold back single-button presses too.
                if (m.Combo.Count == 1 && m.Trigger is MacroTrigger.LongPress or MacroTrigger.DoublePress && pressed(m.Combo[0]))
                    block.Add(m.Combo[0]);
            }
            if (now < slot.TapUntil)
                foreach (var b in m.Combo) inject.Add(b);
        }

        // A combo button stays hidden from the game until it's let go, even after the combo itself breaks up.
        foreach (var b in _swallowed.ToList())
        {
            if (pressed(b)) block.Add(b);
            else _swallowed.Remove(b);
        }
        foreach (var b in block) release(b);
        foreach (var b in inject) press(b);

        var buttons = new HashSet<MacroButton>();
        var sticks = new List<(MacroStick, int, int)>();
        foreach (var slot in _slots)
        {
            if (slot.Run == null) continue;
            if (!Advance(slot.Run, now))
            {
                slot.Run = null;
                continue;
            }
            buttons.UnionWith(slot.Run.Held);
            if (slot.Run.Stick is { } st) sticks.Add(st);
        }
        return (buttons, sticks);
    }

    void Fire(Macro m, Slot slot, long now)
    {
        if (slot.Run != null)
        {
            if (m.Repeat == MacroRepeat.Toggle) slot.Run.Stopping = true; // second press turns it off
            return; // already running
        }
        if (m.Steps.Count == 0) return;
        slot.Run = new Run { Macro = m };
    }

    /// <summary>Moves a run forward; false once it has finished.</summary>
    bool Advance(Run r, long now)
    {
        var m = r.Macro;
        if (r.Stopping)
        {
            Finish(r);
            return false;
        }
        for (int guard = 0; guard < 256; guard++)
        {
            if (r.Step >= m.Steps.Count)
            {
                r.Iteration++;
                bool again = m.Repeat != MacroRepeat.Once || r.Iteration < Math.Max(1, m.Iterations);
                if (!again)
                {
                    Finish(r);
                    return false;
                }
                r.Step = 0;
                r.Started = false;
                if (m.PauseBetweenMs > 0)
                {
                    r.Step = -1; // pause pseudo-step
                    r.StepEnd = now + m.PauseBetweenMs;
                    r.Started = true;
                }
            }
            if (r.Step == -1)
            {
                if (now < r.StepEnd) return true;
                r.Step = 0;
                r.Started = false;
            }

            var s = m.Steps[r.Step];
            if (!r.Started)
            {
                r.Started = true;
                r.StepEnd = now + Math.Max(0, s.Ms);
                StartStep(r, s);
            }
            if (s.Timed && now < r.StepEnd) return true;
            EndStep(r, s);
            r.Step++;
            r.Started = false;
        }
        return true;
    }

    void StartStep(Run r, MacroStep s)
    {
        switch (s.Kind)
        {
            case MacroStepKind.Button:
            case MacroStepKind.ButtonDown:
                r.Held.Add(s.Button);
                break;
            case MacroStepKind.ButtonUp:
                r.Held.Remove(s.Button);
                break;
            case MacroStepKind.Key:
            case MacroStepKind.KeyDown:
                if (KeyCombo.Parse(s.Keys) is { } combo)
                    foreach (var vk in combo.Modifiers.Concat(combo.Key is { } k ? new[] { k } : Array.Empty<ushort>()))
                    {
                        KeyInput.Down(vk);
                        r.KeysDown.Add(vk);
                    }
                break;
            case MacroStepKind.KeyUp:
                if (KeyCombo.Parse(s.Keys) is { } up)
                    foreach (var vk in (up.Key is { } k2 ? new[] { k2 } : Array.Empty<ushort>()).Concat(up.Modifiers.Reverse()))
                    {
                        KeyInput.Up(vk);
                        r.KeysDown.Remove(vk);
                    }
                break;
            case MacroStepKind.Mouse:
                MouseInput.Button(s.Mouse, down: true);
                r.MouseDown.Add(s.Mouse);
                break;
            case MacroStepKind.Stick:
                r.Stick = (s.Stick, s.X, s.Y);
                break;
            case MacroStepKind.Rumble:
                byte v = (byte)Math.Clamp(s.Strength * 255 / 100, 0, 255);
                _rumble(v, v);
                r.Rumbling = true;
                break;
        }
    }

    void EndStep(Run r, MacroStep s)
    {
        switch (s.Kind)
        {
            case MacroStepKind.Button:
                r.Held.Remove(s.Button);
                break;
            case MacroStepKind.Key:
                if (KeyCombo.Parse(s.Keys) is { } combo)
                    foreach (var vk in (combo.Key is { } k ? new[] { k } : Array.Empty<ushort>()).Concat(combo.Modifiers.Reverse()))
                    {
                        KeyInput.Up(vk);
                        r.KeysDown.Remove(vk);
                    }
                break;
            case MacroStepKind.Mouse:
                MouseInput.Button(s.Mouse, down: false);
                r.MouseDown.Remove(s.Mouse);
                break;
            case MacroStepKind.Stick:
                r.Stick = null;
                break;
            case MacroStepKind.Rumble:
                _rumble(0, 0);
                r.Rumbling = false;
                break;
        }
    }

    /// <summary>Lets go of everything a run still holds.</summary>
    void Finish(Run r)
    {
        r.Held.Clear();
        r.Stick = null;
        for (int i = r.KeysDown.Count - 1; i >= 0; i--) KeyInput.Up(r.KeysDown[i]);
        r.KeysDown.Clear();
        foreach (var b in r.MouseDown) MouseInput.Button(b, down: false);
        r.MouseDown.Clear();
        if (r.Rumbling) _rumble(0, 0);
        r.Rumbling = false;
    }

    void Stop(Run r, long now)
    {
        Finish(r);
        foreach (var s in _slots) if (s.Run == r) s.Run = null;
    }

    /// <summary>Stops every macro and releases its keys (controller off / unplugged / macros changed).</summary>
    public void StopAll()
    {
        foreach (var s in _slots)
        {
            if (s.Run != null) Finish(s.Run);
            s.Run = null;
        }
    }
}

static class MouseInput
{
    const uint LeftDown = 0x02, LeftUp = 0x04, RightDown = 0x08, RightUp = 0x10, MiddleDown = 0x20, MiddleUp = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInputData
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public uint Type; // 0 = mouse; MOUSEINPUT is the union's largest member, so this is sizeof(INPUT) (40 on x64)
        public MouseInputData Mi;
    }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);

    public static void Button(MacroMouse b, bool down)
    {
        uint flag = (b, down) switch
        {
            (MacroMouse.Left, true) => LeftDown,
            (MacroMouse.Left, false) => LeftUp,
            (MacroMouse.Right, true) => RightDown,
            (MacroMouse.Right, false) => RightUp,
            (MacroMouse.Middle, true) => MiddleDown,
            _ => MiddleUp,
        };
        var input = new[] { new Input { Type = 0, Mi = new MouseInputData { Flags = flag } } };
        SendInput(1, input, Marshal.SizeOf<Input>());
    }

    const uint XDown = 0x80, XUp = 0x100, Wheel = 0x800;

    /// <summary>Shortcut mouse actions: buttons (including Back / Forward) go down or up; the wheel scrolls one notch.</summary>
    public static void Shortcut(ShortcutMouse m, bool down)
    {
        switch (m)
        {
            case ShortcutMouse.Left: Button(MacroMouse.Left, down); return;
            case ShortcutMouse.Right: Button(MacroMouse.Right, down); return;
            case ShortcutMouse.Middle: Button(MacroMouse.Middle, down); return;
            case ShortcutMouse.WheelUp or ShortcutMouse.WheelDown:
                if (!down) return;
                Send(Wheel, unchecked((uint)(m == ShortcutMouse.WheelUp ? 120 : -120)));
                return;
            default:
                Send(down ? XDown : XUp, m == ShortcutMouse.Back ? 1u : 2u); // XBUTTON1 = back, XBUTTON2 = forward
                return;
        }
    }

    static void Send(uint flags, uint data)
    {
        var input = new[] { new Input { Type = 0, Mi = new MouseInputData { Flags = flags, MouseData = data } } };
        SendInput(1, input, Marshal.SizeOf<Input>());
    }
}
