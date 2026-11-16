using System.Diagnostics;

namespace DSGyro360;

public enum ShortcutKind
{
    /// <summary>Press a key combination, e.g. "Alt+Tab".</summary>
    Keys,
    /// <summary>Open a program, file, folder or web address.</summary>
    Run,
    /// <summary>Press a mouse button (held while the combo button is held) or scroll the wheel.</summary>
    Mouse,
    /// <summary>Press a gamepad button on the virtual Xbox pad (held while the combo button is held).</summary>
    Pad,
}

public enum ShortcutMouse { Left, Right, Middle, Back, Forward, WheelUp, WheelDown }

/// <summary>PS + <see cref="Button"/> runs an action. Replaced (never mutated) in the live settings.</summary>
public sealed class Shortcut
{
    public ChordButton Button { get; set; } = ChordButton.Cross;
    public ShortcutKind Kind { get; set; } = ShortcutKind.Keys;
    /// <summary>Key combination for <see cref="ShortcutKind.Keys"/>, e.g. "Ctrl+Shift+Escape".</summary>
    public string Keys { get; set; } = "Alt+Tab";
    /// <summary>Program, file, folder or URL for <see cref="ShortcutKind.Run"/>.</summary>
    public string Target { get; set; } = "";
    /// <summary>Mouse button or wheel for <see cref="ShortcutKind.Mouse"/>.</summary>
    public ShortcutMouse Mouse { get; set; } = ShortcutMouse.Left;
    /// <summary>Xbox button for <see cref="ShortcutKind.Pad"/>.</summary>
    public MacroButton Pad { get; set; } = MacroButton.A;

    public Shortcut Clone() => (Shortcut)MemberwiseClone();

    public static string MouseName(ShortcutMouse m) => m switch
    {
        ShortcutMouse.Left => "Left click",
        ShortcutMouse.Right => "Right click",
        ShortcutMouse.Middle => "Middle click",
        ShortcutMouse.Back => "Back button",
        ShortcutMouse.Forward => "Forward button",
        ShortcutMouse.WheelUp => "Wheel up",
        _ => "Wheel down",
    };

    /// <summary>What the shortcut presses, for the button that opens the picker.</summary>
    public string BindingText => Kind switch
    {
        ShortcutKind.Mouse => MouseName(Mouse),
        ShortcutKind.Pad => "Pad " + MacroStep.ButtonName(Pad),
        ShortcutKind.Run => Target,
        _ => string.IsNullOrWhiteSpace(Keys) ? "(none)" : Keys,
    };
}

/// <summary>A parsed key combination: modifiers held around one main key (or modifiers alone, e.g. "Win").</summary>
public sealed record KeyCombo(ushort[] Modifiers, ushort? Key)
{
    const ushort VkShift = 0x10, VkCtrl = 0x11, VkAlt = 0x12, VkWin = 0x5B;

    static readonly Dictionary<string, ushort> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = VkCtrl, ["Control"] = VkCtrl,
        ["Shift"] = VkShift,
        ["Alt"] = VkAlt,
        ["Win"] = VkWin, ["Windows"] = VkWin, ["LWin"] = VkWin,
    };

    static readonly Dictionary<string, Keys> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = System.Windows.Forms.Keys.Escape,
        ["Del"] = System.Windows.Forms.Keys.Delete,
        ["Ins"] = System.Windows.Forms.Keys.Insert,
        ["PrtScn"] = System.Windows.Forms.Keys.PrintScreen,
        ["PgUp"] = System.Windows.Forms.Keys.PageUp,
        ["PgDn"] = System.Windows.Forms.Keys.PageDown,
    };

    /// <summary>Parses "Ctrl+Shift+Escape"-style text; null if any part is unknown.</summary>
    public static KeyCombo? Parse(string text)
    {
        var mods = new List<ushort>();
        ushort? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (ModifierNames.TryGetValue(raw, out var m))
            {
                if (!mods.Contains(m)) mods.Add(m);
                continue;
            }
            if (key != null) return null; // only one main key
            if (Aliases.TryGetValue(raw, out var alias)) key = (ushort)alias;
            else if (raw.Length == 1 && char.IsDigit(raw[0])) key = (ushort)raw[0]; // "1" -> D1
            else if (Enum.TryParse<Keys>(raw, true, out var k) && (int)k is > 0 and < 256) key = (ushort)k;
            else return null;
        }
        return mods.Count == 0 && key == null ? null : new KeyCombo(mods.ToArray(), key);
    }

    /// <summary>Text for a combination pressed in the recorder box.</summary>
    public static string Format(bool ctrl, bool shift, bool alt, bool win, Keys key)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        if (win) parts.Add("Win");
        if (key != System.Windows.Forms.Keys.None)
            parts.Add(key is >= System.Windows.Forms.Keys.D0 and <= System.Windows.Forms.Keys.D9
                ? ((char)key).ToString()
                : key.ToString());
        return string.Join("+", parts);
    }

    public static bool IsModifierKey(Keys k) => k is System.Windows.Forms.Keys.ControlKey or System.Windows.Forms.Keys.ShiftKey
        or System.Windows.Forms.Keys.Menu or System.Windows.Forms.Keys.LWin or System.Windows.Forms.Keys.RWin
        or System.Windows.Forms.Keys.LControlKey or System.Windows.Forms.Keys.RControlKey
        or System.Windows.Forms.Keys.LShiftKey or System.Windows.Forms.Keys.RShiftKey
        or System.Windows.Forms.Keys.LMenu or System.Windows.Forms.Keys.RMenu;
}

static class ShortcutPresets
{
    /// <summary>Common actions; Win and Alt+Tab combos can't be typed into the recorder (Windows takes them).</summary>
    public static readonly (string Text, string Keys)[] All =
    {
        ("Switch window (Alt+Tab)", "Alt+Tab"),
        ("Task view (Win+Tab)", "Win+Tab"),
        ("Show desktop (Win+D)", "Win+D"),
        ("Start menu (Win)", "Win"),
        ("Close app (Alt+F4)", "Alt+F4"),
        ("Task Manager", "Ctrl+Shift+Escape"),
        ("Game Bar (Win+G)", "Win+G"),
        ("Screenshot (Game Bar)", "Win+Alt+PrintScreen"),
        ("Record start / stop (Game Bar)", "Win+Alt+R"),
        ("Screenshot (NVIDIA)", "Alt+F1"),
        ("Record start / stop (NVIDIA)", "Alt+F9"),
        ("On-screen keyboard", "Win+Ctrl+O"),
        ("Fullscreen (Alt+Enter)", "Alt+Enter"),
        ("Escape", "Escape"),
        ("Alt+X", "Alt+X"),
        ("Alt+S", "Alt+S"),
        ("Alt+W", "Alt+W"),
        ("Alt+Q", "Alt+Q"),
        ("Volume up", "VolumeUp"),
        ("Volume down", "VolumeDown"),
        ("Mute", "VolumeMute"),
        ("Play / pause", "MediaPlayPause"),
        ("Next track", "MediaNextTrack"),
        ("Previous track", "MediaPreviousTrack"),
    };
}

static class ShortcutRunner
{
    /// <summary>
    /// Opens a program, file, folder or URL as the signed-in user. The app itself runs as administrator;
    /// going through Explorer keeps launched programs from inheriting that.
    /// </summary>
    public static void Launch(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target.Trim().Trim('"') + "\"")
                {
                    UseShellExecute = false,
                })?.Dispose();
            }
            catch { /* missing program: nothing to do from a controller */ }
        });
    }
}
