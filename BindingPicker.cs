namespace Gyro2stick;

/// <summary>
/// Steam-style binding picker: a keyboard, a mouse and an Xbox gamepad drawn as buttons. Click what the shortcut
/// should press. On the keyboard, Ctrl / Shift / Alt / Win toggle on, then a key completes the combination.
/// </summary>
sealed class BindingPicker : Form
{
    public ShortcutKind Kind { get; private set; }
    public string Keys { get; private set; }
    public ShortcutMouse Mouse { get; private set; }
    public MacroButton Pad { get; private set; }

    const int U = 40;      // one key width (96-dpi pixels)
    const int KeyH = 36;
    const int Gap = 4;

    readonly Panel _keyboard = new(), _mouse = new(), _pad = new();
    readonly Button _tabKeyboard = new(), _tabMouse = new(), _tabPad = new();
    readonly Label _current = new();
    readonly ToolTip _tips = new();

    // Keyboard state: modifiers held down by toggle, plus the main key (a Keys name KeyCombo can parse).
    readonly HashSet<string> _mods = new(StringComparer.OrdinalIgnoreCase);
    string? _key;
    readonly List<(Button Button, string Name, bool Modifier)> _keyButtons = new();
    readonly List<(Button Button, ShortcutMouse Value)> _mouseButtons = new();
    readonly List<(Button Button, MacroButton Value)> _padButtons = new();

    public BindingPicker(Shortcut current)
    {
        Kind = current.Kind == ShortcutKind.Run ? ShortcutKind.Keys : current.Kind;
        Keys = current.Keys;
        Mouse = current.Mouse;
        Pad = current.Pad;
        LoadKeys(current.Keys);

        Text = "Choose what the shortcut presses";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None; // positions below are in 96-dpi pixels, scaled at the end
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(860, 420);

        int x = 16;
        foreach (var (tab, text, kind) in new[] { (_tabKeyboard, "Keyboard", ShortcutKind.Keys), (_tabMouse, "Mouse", ShortcutKind.Mouse), (_tabPad, "Gamepad", ShortcutKind.Pad) })
        {
            tab.Text = text;
            tab.SetBounds(x, 10, 110, 34);
            tab.Click += (_, _) => ShowTab(kind);
            Controls.Add(tab);
            x += 116;
        }
        _current.SetBounds(400, 18, 444, 24);
        _current.TextAlign = ContentAlignment.MiddleRight;
        Controls.Add(_current);

        foreach (var p in new[] { _keyboard, _mouse, _pad })
        {
            p.SetBounds(16, 56, 828, 300);
            Controls.Add(p);
        }
        BuildKeyboard();
        BuildMouse();
        BuildPad();

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        ok.SetBounds(744, 372, 100, 34);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(636, 372, 100, 34);
        var clear = new Button { Text = "Clear keys" };
        clear.SetBounds(16, 372, 110, 34);
        clear.Click += (_, _) => { _mods.Clear(); _key = null; Kind = ShortcutKind.Keys; UpdateView(); };
        _tips.SetToolTip(clear, "Clear the keyboard combination");
        Controls.AddRange(new Control[] { ok, cancel, clear });
        AcceptButton = ok;
        CancelButton = cancel;

        DarkTheme.Apply(this);
        BackColor = DarkTheme.Background;
        ForeColor = DarkTheme.Text;
        ok.Tag = DarkTheme.KeepColors;
        ok.BackColor = DarkTheme.Primary;
        ok.ForeColor = DarkTheme.OnPrimary;
        float k = DeviceDpi / 96f;
        if (k > 1.01f) Scale(new SizeF(k, k));
        ShowTab(Kind);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTheme.ApplyTitleBar(this);
    }

    void ShowTab(ShortcutKind kind)
    {
        _keyboard.Visible = kind == ShortcutKind.Keys;
        _mouse.Visible = kind == ShortcutKind.Mouse;
        _pad.Visible = kind == ShortcutKind.Pad;
        DarkTheme.SetTab(_tabKeyboard, kind == ShortcutKind.Keys);
        DarkTheme.SetTab(_tabMouse, kind == ShortcutKind.Mouse);
        DarkTheme.SetTab(_tabPad, kind == ShortcutKind.Pad);
    }

    // ---------- keyboard ----------

    void LoadKeys(string text)
    {
        foreach (var part in (text ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) _mods.Add("Ctrl");
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) _mods.Add("Shift");
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) _mods.Add("Alt");
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase)) _mods.Add("Win");
            else _key = part;
        }
    }

    string ComboText()
    {
        var parts = new List<string>();
        foreach (var m in new[] { "Ctrl", "Shift", "Alt", "Win" }) if (_mods.Contains(m)) parts.Add(m);
        if (_key != null) parts.Add(_key);
        return string.Join("+", parts);
    }

    static ushort? Vk(string name) => KeyCombo.Parse(name)?.Key;

    void BuildKeyboard()
    {
        // (label, Keys name, width in keys); a null name is a gap.
        var rows = new (string Label, string? Name, double W)[][]
        {
            new (string, string?, double)[] { ("Esc", "Escape", 1), ("", null, 0.5), ("F1", "F1", 1), ("F2", "F2", 1), ("F3", "F3", 1), ("F4", "F4", 1), ("", null, 0.5),
                ("F5", "F5", 1), ("F6", "F6", 1), ("F7", "F7", 1), ("F8", "F8", 1), ("", null, 0.5), ("F9", "F9", 1), ("F10", "F10", 1), ("F11", "F11", 1), ("F12", "F12", 1),
                ("", null, 0.25), ("PrtSc", "PrintScreen", 1), ("Pause", "Pause", 1) },
            new (string, string?, double)[] { ("`", "Oemtilde", 1), ("1", "1", 1), ("2", "2", 1), ("3", "3", 1), ("4", "4", 1), ("5", "5", 1), ("6", "6", 1), ("7", "7", 1),
                ("8", "8", 1), ("9", "9", 1), ("0", "0", 1), ("-", "OemMinus", 1), ("=", "Oemplus", 1), ("Backspace", "Back", 2),
                ("", null, 0.25), ("Ins", "Insert", 1), ("Home", "Home", 1) },
            new (string, string?, double)[] { ("Tab", "Tab", 1.5), ("Q", "Q", 1), ("W", "W", 1), ("E", "E", 1), ("R", "R", 1), ("T", "T", 1), ("Y", "Y", 1), ("U", "U", 1),
                ("I", "I", 1), ("O", "O", 1), ("P", "P", 1), ("[", "OemOpenBrackets", 1), ("]", "OemCloseBrackets", 1), ("\\", "OemPipe", 1.5),
                ("", null, 0.25), ("Del", "Delete", 1), ("End", "End", 1) },
            new (string, string?, double)[] { ("Caps", "CapsLock", 1.75), ("A", "A", 1), ("S", "S", 1), ("D", "D", 1), ("F", "F", 1), ("G", "G", 1), ("H", "H", 1), ("J", "J", 1),
                ("K", "K", 1), ("L", "L", 1), (";", "OemSemicolon", 1), ("'", "OemQuotes", 1), ("Enter", "Enter", 2.25),
                ("", null, 0.25), ("PgUp", "PageUp", 1), ("PgDn", "PageDown", 1) },
            new (string, string?, double)[] { ("Shift", "Shift", 2.25), ("Z", "Z", 1), ("X", "X", 1), ("C", "C", 1), ("V", "V", 1), ("B", "B", 1), ("N", "N", 1), ("M", "M", 1),
                (",", "Oemcomma", 1), (".", "OemPeriod", 1), ("/", "OemQuestion", 1), ("Shift", "Shift", 2.75),
                ("", null, 0.25), ("", null, 0.5), ("▲", "Up", 1) },
            new (string, string?, double)[] { ("Ctrl", "Ctrl", 1.25), ("Win", "Win", 1.25), ("Alt", "Alt", 1.25), ("Space", "Space", 6.25), ("Alt", "Alt", 1.25),
                ("Win", "Win", 1.25), ("Menu", "Apps", 1.25), ("Ctrl", "Ctrl", 1.25),
                ("", null, 0.25), ("◀", "Left", 1), ("▼", "Down", 1), ("▶", "Right", 1) },
        };
        int y = 0;
        for (int r = 0; r < rows.Length; r++)
        {
            double x = 0;
            foreach (var (label, name, w) in rows[r])
            {
                int px = (int)Math.Round(x * (U + Gap)), width = (int)Math.Round(w * (U + Gap)) - Gap;
                x += w;
                if (name == null) continue;
                AddKey(label, name, px, y, width);
            }
            y += KeyH + Gap + (r == 0 ? 10 : 0);
        }

        // Media keys, in a column on the right.
        int mx = 760, my = 0;
        _keyboard.Controls.Add(new Label { Text = "Media", Tag = DarkTheme.Subtle, Left = mx, Top = my, Width = 68, Height = 18 });
        my += 22;
        foreach (var (label, name) in new[] { ("Vol +", "VolumeUp"), ("Vol −", "VolumeDown"), ("Mute", "VolumeMute"), ("Play", "MediaPlayPause"), ("Next", "MediaNextTrack"), ("Prev", "MediaPreviousTrack") })
        {
            AddKey(label, name, mx, my, 68);
            my += KeyH + Gap;
        }
        _keyboard.Controls.Add(new Label
        {
            Text = "Click Ctrl, Shift, Alt or Win to add them, then click a key. For example Alt, then Tab, gives Alt+Tab.",
            Tag = DarkTheme.Subtle,
            Left = 0,
            Top = y + 6,
            Width = 740,
            Height = 22,
        });
    }

    void AddKey(string label, string name, int x, int y, int width)
    {
        bool modifier = name is "Ctrl" or "Shift" or "Alt" or "Win";
        var b = new Button { Text = label, Tag = DarkTheme.KeepColors, Font = new Font("Segoe UI", label.Length > 3 ? 8.5f : 9.5f) };
        b.SetBounds(x, y, width, KeyH);
        DarkTheme.SetRadius(b, 6);
        b.Click += (_, _) =>
        {
            Kind = ShortcutKind.Keys;
            if (modifier)
            {
                if (!_mods.Remove(name)) _mods.Add(name);
            }
            else _key = string.Equals(_key, name, StringComparison.OrdinalIgnoreCase) ? null : name;
            UpdateView();
        };
        _keyButtons.Add((b, name, modifier));
        _keyboard.Controls.Add(b);
    }

    // ---------- mouse ----------

    void BuildMouse()
    {
        // A mouse seen from above: two main buttons with the wheel between, side buttons on the left.
        void Add(ShortcutMouse value, string text, int x, int y, int w, int h, float radius)
        {
            var b = new Button { Text = text, Tag = DarkTheme.KeepColors };
            b.SetBounds(x, y, w, h);
            DarkTheme.SetRadius(b, radius);
            b.Click += (_, _) => { Kind = ShortcutKind.Mouse; Mouse = value; UpdateView(); };
            _mouseButtons.Add((b, value));
            _mouse.Controls.Add(b);
        }
        int cx = 300;
        Add(ShortcutMouse.Left, "Left click", cx, 10, 120, 140, 18);
        Add(ShortcutMouse.WheelUp, "Wheel ▲", cx + 126, 10, 76, 44, 10);
        Add(ShortcutMouse.Middle, "Middle", cx + 126, 58, 76, 44, 10);
        Add(ShortcutMouse.WheelDown, "Wheel ▼", cx + 126, 106, 76, 44, 10);
        Add(ShortcutMouse.Right, "Right click", cx + 208, 10, 120, 140, 18);
        Add(ShortcutMouse.Forward, "Forward", cx - 120, 160, 110, 40, 10);
        Add(ShortcutMouse.Back, "Back", cx - 120, 206, 110, 40, 10);
        _mouse.Controls.Add(new Label
        {
            Text = "Buttons stay pressed while you hold the controller button. Each wheel press scrolls one notch.",
            Tag = DarkTheme.Subtle,
            Left = 0,
            Top = 268,
            Width = 800,
            Height = 22,
        });
    }

    // ---------- gamepad ----------

    void BuildPad()
    {
        void Add(MacroButton value, string text, int x, int y, int w, int h, float radius = 10)
        {
            var b = new Button { Text = text, Tag = DarkTheme.KeepColors };
            b.SetBounds(x, y, w, h);
            DarkTheme.SetRadius(b, radius);
            b.Click += (_, _) => { Kind = ShortcutKind.Pad; Pad = value; UpdateView(); };
            _tips.SetToolTip(b, MacroStep.ButtonName(value));
            _padButtons.Add((b, value));
            _pad.Controls.Add(b);
        }
        // Shoulders
        Add(MacroButton.LT, "LT", 120, 0, 100, 34);
        Add(MacroButton.LB, "LB", 120, 40, 100, 34);
        Add(MacroButton.RT, "RT", 608, 0, 100, 34);
        Add(MacroButton.RB, "RB", 608, 40, 100, 34);
        // Centre
        Add(MacroButton.Back, "View", 318, 104, 70, 34);
        Add(MacroButton.Guide, "Guide", 394, 92, 60, 60, 30);
        Add(MacroButton.Start, "Menu", 460, 104, 70, 34);
        // Left stick and D-pad
        Add(MacroButton.LS, "LS\nclick", 130, 96, 74, 74, 37);
        Add(MacroButton.Up, "▲", 250, 170, 46, 40, 8);
        Add(MacroButton.Left, "◀", 202, 214, 46, 40, 8);
        Add(MacroButton.Right, "▶", 298, 214, 46, 40, 8);
        Add(MacroButton.Down, "▼", 250, 258, 46, 40, 8);
        // Face buttons and right stick
        Add(MacroButton.Y, "Y", 630, 96, 46, 46, 23);
        Add(MacroButton.X, "X", 582, 144, 46, 46, 23);
        Add(MacroButton.B, "B", 678, 144, 46, 46, 23);
        Add(MacroButton.A, "A", 630, 192, 46, 46, 23);
        Add(MacroButton.RS, "RS\nclick", 494, 200, 74, 74, 37);
    }

    // ---------- state ----------

    void UpdateView()
    {
        if (Kind == ShortcutKind.Keys) Keys = ComboText();
        var keyVk = _key == null ? null : Vk(_key);
        foreach (var (b, name, modifier) in _keyButtons)
        {
            bool on = Kind == ShortcutKind.Keys && (modifier ? _mods.Contains(name) : keyVk != null && Vk(name) == keyVk);
            Paint(b, on);
        }
        foreach (var (b, value) in _mouseButtons) Paint(b, Kind == ShortcutKind.Mouse && Mouse == value);
        foreach (var (b, value) in _padButtons) Paint(b, Kind == ShortcutKind.Pad && Pad == value);
        _current.Text = "Presses:  " + Kind switch
        {
            ShortcutKind.Mouse => "Mouse " + Shortcut.MouseName(Mouse),
            ShortcutKind.Pad => "Gamepad " + MacroStep.ButtonName(Pad),
            _ => Keys.Length == 0 ? "nothing yet" : Keys,
        };
    }

    static void Paint(Button b, bool on)
    {
        b.BackColor = on ? DarkTheme.Primary : DarkTheme.Surface;
        b.ForeColor = on ? DarkTheme.OnPrimary : DarkTheme.Text;
        b.Invalidate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateView();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UpdateView();
    }
}
