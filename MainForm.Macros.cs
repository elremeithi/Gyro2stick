namespace Gyro2stick;

/// <summary>The Macros page: macro list, step palette, flow-chart view and the selected block's properties.</summary>
public sealed partial class MainForm
{
    MacroFlowView _flow = new();
    ListBox _macroList = new();
    FlowLayoutPanel _macroProps = new();
    CheckBox _macroEnabled = new();
    Label _macroInfo = new();
    bool _macroPageScaled;
    bool _syncingMacros;

    Macro? CurrentMacro => _macroList.SelectedIndex >= 0 && _macroList.SelectedIndex < _pending.Macros.Count
        ? _pending.Macros[_macroList.SelectedIndex] : null;

    // Grouped for the palette; the group name is shown above its first step.
    static readonly (string Group, string Text, MacroStepKind Kind, string Tip)[] Palette =
    {
        ("Controller", "Tap a button", MacroStepKind.Button, "Press a controller button and let go after the set time."),
        ("Controller", "Hold a button", MacroStepKind.ButtonDown, "Press a controller button and keep it down, until a \"Let go\" step or the end."),
        ("Controller", "Let go of a button", MacroStepKind.ButtonUp, "Let go of a button held by a \"Hold a button\" step."),
        ("Controller", "Push a stick", MacroStepKind.Stick, "Push a stick in a direction for the set time."),
        ("Keyboard and mouse", "Tap a key", MacroStepKind.Key, "Press a keyboard key or combination (e.g. Ctrl+C) for the set time."),
        ("Keyboard and mouse", "Hold a key", MacroStepKind.KeyDown, "Press keys and keep them down, until a \"Let go of a key\" step or the end."),
        ("Keyboard and mouse", "Let go of a key", MacroStepKind.KeyUp, "Let go of keys held by a \"Hold a key\" step."),
        ("Keyboard and mouse", "Click the mouse", MacroStepKind.Mouse, "Click a mouse button."),
        ("Timing and extras", "Wait", MacroStepKind.Wait, "Pause before the next step."),
        ("Timing and extras", "Rumble", MacroStepKind.Rumble, "Vibrate the controller."),
    };

    Control BuildMacrosGroup()
    {
        _flow = new MacroFlowView();
        _macroList = new ListBox();
        _macroProps = new FlowLayoutPanel();
        _macroEnabled = new CheckBox();
        _macroInfo = new Label();
        _macroPageScaled = false;

        var group = new GroupBox
        {
            Text = "Macros: press a button combo to play a sequence of buttons, keys and more",
            AutoSize = true,
            Padding = new Padding(4, 0, 4, 1),
        };
        var grid = new TableLayoutPanel { ColumnCount = 4, RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };

        // 0: how it works, in three steps
        var guide = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        foreach (var (n, text) in new[]
                 {
                     ("1", "Click New and pick an example (or a blank one)"),
                     ("2", "Click the purple block to choose the buttons that start it"),
                     ("3", "Add what it does from \"Add a step\", then click Apply"),
                 })
        {
            guide.Controls.Add(new Label
            {
                Text = n,
                AutoSize = false,
                Size = new Size(20, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(3, 1, 3, 1),
                Tag = DarkTheme.KeepColors,
                BackColor = DarkTheme.PrimaryContainer,
                ForeColor = DarkTheme.OnPrimaryContainer,
            });
            guide.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 3, 14, 1) });
        }
        grid.Controls.Add(guide, 0, 0);
        grid.SetColumnSpan(guide, 4);

        // 1: macro list
        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        left.Controls.Add(new Label { Text = "Your macros", AutoSize = true, Margin = new Padding(3, 3, 3, 3) });
        var tt = new ToolTip();
        var add = new Button { Text = "+  New macro…", Size = new Size(204, 28), Margin = new Padding(3, 1, 3, 3), Tag = DarkTheme.KeepColors,
            BackColor = DarkTheme.Primary, ForeColor = DarkTheme.OnPrimary, Font = new Font(Font, FontStyle.Bold) };
        var templates = new ContextMenuStrip { ShowItemToolTips = true };
        foreach (var (title, tip, make) in Macro.Templates)
        {
            var item = templates.Items.Add(title, null, (_, _) => AddMacro(make()));
            item.ToolTipText = tip;
        }
        DarkTheme.Apply(templates);
        add.Click += (_, _) => templates.Show(add, new Point(0, add.Height));
        tt.SetToolTip(add, "Start from a ready-made example, or a blank macro");
        left.Controls.Add(add);
        _macroList.Size = new Size(210, 300);
        _macroList.IntegralHeight = false;
        _macroList.SelectedIndexChanged += (_, _) => { if (!_syncingMacros) ShowMacro(); };
        left.Controls.Add(_macroList);
        var listButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var dup = new Button { Text = "Duplicate", Size = new Size(110, 30) };
        var del = new Button { Text = "Delete", Size = new Size(110, 30) };
        dup.Click += (_, _) => { if (CurrentMacro is { } m) AddMacro(WithName(m.Clone(), m.Name + " copy")); };
        del.Click += (_, _) => DeleteMacro();
        tt.SetToolTip(dup, "Make a copy of this macro");
        tt.SetToolTip(del, "Delete this macro");
        listButtons.Controls.AddRange(new Control[] { dup, del });
        left.Controls.Add(listButtons);
        _macroEnabled.Text = "Enabled";
        _macroEnabled.AutoSize = true;
        _macroEnabled.CheckedChanged += (_, _) =>
        {
            if (_syncingMacros || CurrentMacro is not { } m) return;
            m.Enabled = _macroEnabled.Checked;
            MacroEdited();
        };
        left.Controls.Add(_macroEnabled);
        left.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(230, 0),
            Tag = DarkTheme.Subtle,
            Margin = new Padding(3, 8, 3, 3),
            Text = "Macros belong to the profile in use. Click Apply to make changes live.",
        });
        grid.Controls.Add(left, 0, 1);

        // 2: step palette, grouped
        var palette = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        palette.Controls.Add(new Label { Text = "Add a step", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 0) });
        string? lastGroup = null;
        foreach (var (groupName, text, kind, tip) in Palette)
        {
            if (groupName != lastGroup)
            {
                palette.Controls.Add(new Label { Text = groupName, AutoSize = true, Tag = DarkTheme.Subtle, Margin = new Padding(3, 6, 3, 1) });
                lastGroup = groupName;
            }
            var b = new Button { Text = "+  " + text, Size = new Size(156, 24), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 0, 3, 0) };
            b.Click += (_, _) => AddStep(kind);
            tt.SetToolTip(b, tip);
            palette.Controls.Add(b);
        }
        palette.Controls.Add(new Label { Text = "Selected step", AutoSize = true, Tag = DarkTheme.Subtle, Margin = new Padding(3, 10, 3, 1) });
        var moves = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var up = new Button { Text = "▲  Up", Size = new Size(76, 24), Margin = new Padding(3, 0, 2, 0) };
        var down = new Button { Text = "▼  Down", Size = new Size(76, 24), Margin = new Padding(1, 0, 3, 0) };
        moves.Controls.AddRange(new Control[] { up, down });
        var remove = new Button { Text = "✕  Delete step", Size = new Size(156, 24), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 0, 3, 0) };
        up.Click += (_, _) => MoveStep(-1);
        down.Click += (_, _) => MoveStep(1);
        remove.Click += (_, _) => DeleteStep();
        tt.SetToolTip(up, "Move the selected step earlier (or drag it in the chart)");
        tt.SetToolTip(down, "Move the selected step later (or drag it in the chart)");
        palette.Controls.AddRange(new Control[] { moves, remove });
        grid.Controls.Add(palette, 1, 1);

        // 3: flow chart, with what the macro does in words underneath
        var middle = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
        _flow.Size = new Size(430, 392);
        _flow.Margin = Padding.Empty;
        _flow.SelectionChanged += ShowProps;
        _flow.StepsReordered += MacroEdited;
        _flow.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) DeleteStep(); };
        middle.Controls.Add(_flow);
        _macroInfo.AutoSize = false;
        _macroInfo.Size = new Size(430, 56);
        _macroInfo.Margin = new Padding(0, 6, 0, 0);
        _macroInfo.Tag = DarkTheme.Subtle;
        middle.Controls.Add(_macroInfo);
        grid.Controls.Add(middle, 2, 1);

        // 4: properties of the selected block
        _macroProps.FlowDirection = FlowDirection.TopDown;
        _macroProps.WrapContents = false;
        _macroProps.AutoScroll = true;
        _macroProps.Size = new Size(320, 452);
        _macroProps.Margin = new Padding(0, 6, 0, 0);
        DarkTheme.DarkScrollbars(_macroProps);
        DarkTheme.DarkScrollbars(_flow);
        DarkTheme.DarkScrollbars(_macroList);
        grid.Controls.Add(_macroProps, 3, 1);

        group.Controls.Add(grid);
        RefreshMacroList(0);
        return group;
    }

    static Macro WithName(Macro m, string name)
    {
        m.Name = name;
        return m;
    }

    void AddMacro(Macro m)
    {
        _pending.Macros.Add(m);
        RefreshMacroList(_pending.Macros.Count - 1);
        Changed();
    }

    void DeleteMacro()
    {
        if (CurrentMacro is not { } m) return;
        if (MessageBox.Show(this, $"Delete the macro \"{m.Name}\"?", "Macros", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        int i = _macroList.SelectedIndex;
        _pending.Macros.Remove(m);
        RefreshMacroList(Math.Min(i, _pending.Macros.Count - 1));
        Changed();
    }

    void RefreshMacroList(int select)
    {
        _syncingMacros = true;
        _macroList.BeginUpdate();
        _macroList.Items.Clear();
        foreach (var m in _pending.Macros)
            _macroList.Items.Add((m.Enabled ? "" : "(off) ") + m.Name + "   ·   " + m.ComboText);
        _macroList.EndUpdate();
        _macroList.SelectedIndex = _pending.Macros.Count == 0 ? -1 : Math.Clamp(select, 0, _pending.Macros.Count - 1);
        _syncingMacros = false;
        ShowMacro();
    }

    void ShowMacro()
    {
        var m = CurrentMacro;
        _syncingMacros = true;
        _macroEnabled.Enabled = m != null;
        _macroEnabled.Checked = m?.Enabled ?? false;
        _syncingMacros = false;
        _flow.Macro = m;
        UpdateMacroInfo();
        ShowProps();
    }

    void UpdateMacroInfo() => _macroInfo.Text = CurrentMacro is { } m
        ? "In words: " + m.Describe()
        : "No macro selected.";

    /// <summary>Something in the current macro changed: redraw, relabel, mark unapplied.</summary>
    void MacroEdited()
    {
        _flow.UpdateScrollSize();
        _flow.Invalidate();
        int i = _macroList.SelectedIndex;
        if (i >= 0 && CurrentMacro is { } m)
        {
            _syncingMacros = true;
            _macroList.Items[i] = (m.Enabled ? "" : "(off) ") + m.Name + "   ·   " + m.ComboText;
            _syncingMacros = false;
        }
        UpdateMacroInfo();
        Changed();
    }

    void AddStep(MacroStepKind kind)
    {
        if (CurrentMacro is not { } m)
        {
            AddMacro(new Macro());
            m = CurrentMacro!;
        }
        var step = new MacroStep
        {
            Kind = kind,
            Ms = kind switch
            {
                MacroStepKind.Wait => 100,
                MacroStepKind.Stick or MacroStepKind.Rumble => 250,
                _ => 60,
            },
        };
        // Release / key-up steps default to what the last hold step held.
        if (kind == MacroStepKind.ButtonUp && m.Steps.LastOrDefault(s => s.Kind == MacroStepKind.ButtonDown) is { } held) step.Button = held.Button;
        if (kind == MacroStepKind.KeyUp && m.Steps.LastOrDefault(s => s.Kind == MacroStepKind.KeyDown) is { } keys) step.Keys = keys.Keys;
        int sel = _flow.Selected;
        int at = sel >= 0 && sel < m.Steps.Count ? sel + 1 : sel == -1 ? 0 : m.Steps.Count;
        m.Steps.Insert(at, step);
        _flow.UpdateScrollSize();
        _flow.Selected = at;
        ShowProps();
        MacroEdited();
    }

    void MoveStep(int delta)
    {
        if (CurrentMacro is not { } m) return;
        int i = _flow.Selected, j = i + delta;
        if (i < 0 || i >= m.Steps.Count || j < 0 || j >= m.Steps.Count) return;
        (m.Steps[i], m.Steps[j]) = (m.Steps[j], m.Steps[i]);
        _flow.Selected = j;
        MacroEdited();
    }

    void DeleteStep()
    {
        if (CurrentMacro is not { } m) return;
        int i = _flow.Selected;
        if (i < 0 || i >= m.Steps.Count) return;
        m.Steps.RemoveAt(i);
        _flow.UpdateScrollSize();
        _flow.Selected = Math.Min(i, m.Steps.Count - 1);
        ShowProps();
        MacroEdited();
    }

    // ---------- properties panel ----------

    void ShowProps()
    {
        _macroProps.SuspendLayout();
        foreach (var c in _macroProps.Controls.Cast<Control>().ToList()) c.Dispose();
        _macroProps.Controls.Clear();

        var m = CurrentMacro;
        if (m == null)
        {
            PropNote("No macros yet. Click \"+ New macro…\" and pick an example to see how one works, or a blank macro to build your own.");
        }
        else if (_flow.Selected < 0)
        {
            PropHeader("What starts it");
            PropText("Name", m.Name, v => { m.Name = v; MacroEdited(); });
            var combos = Enum.GetValues<ChordButton>().Where(b => b != ChordButton.None).ToArray();
            for (int i = 0; i < 3; i++)
            {
                int slot = i;
                var items = (i == 0 ? combos : combos.Prepend(ChordButton.None)).ToArray();
                var current = slot < m.Combo.Count ? m.Combo[slot] : ChordButton.None;
                PropChoice(i == 0 ? "Button" : "and (optional)", items, current, b => b == ChordButton.None ? "—" : Macro.ComboName(b), v =>
                {
                    var list = m.Combo.ToList();
                    while (list.Count <= slot) list.Add(ChordButton.None);
                    list[slot] = v;
                    m.Combo = list.Where(b => b != ChordButton.None).Distinct().ToList();
                    MacroEdited();
                });
            }
            PropChoice("Starts", Enum.GetValues<MacroTrigger>(), m.Trigger, t => t switch
            {
                MacroTrigger.LongPress => "When held for a while",
                MacroTrigger.DoublePress => "When pressed twice",
                MacroTrigger.Release => "When let go",
                _ => "When pressed",
            }, v => { m.Trigger = v; MacroEdited(); ShowProps(); });
            PropNote(m.Trigger switch
            {
                MacroTrigger.LongPress => "Starts once you've held the buttons for the time below. A quick tap still reaches the game as normal.",
                MacroTrigger.DoublePress => "Starts when you press the buttons twice within the time below. A single press still reaches the game.",
                MacroTrigger.Release => "Starts when you let go of the buttons.",
                _ => "Starts as soon as all the buttons are down.",
            });
            if (m.Trigger is MacroTrigger.LongPress or MacroTrigger.DoublePress)
                PropNumber(m.Trigger == MacroTrigger.LongPress ? "Hold for (ms)" : "Within (ms)", 100, 2000, m.TriggerMs, v => { m.TriggerMs = v; MacroEdited(); });
            PropCheck("Don't let the game see these buttons", m.BlockSource, v => { m.BlockSource = v; MacroEdited(); });
            PropNote("Ticked: while the whole combo is held, the game doesn't get those buttons, so they only run the macro. " +
                     "Each button pressed on its own still works in the game.");
            AddRepeatProps(m);
        }
        else if (_flow.Selected >= m.Steps.Count)
        {
            PropHeader("When it ends");
            AddRepeatProps(m);
        }
        else
        {
            var s = m.Steps[_flow.Selected];
            PropHeader($"Step {_flow.Selected + 1}");
            var kinds = Palette.Select(p => p.Kind).ToArray();
            PropChoice("Type", kinds, s.Kind, k => Palette.First(p => p.Kind == k).Text, v => { s.Kind = v; MacroEdited(); ShowProps(); });
            switch (s.Kind)
            {
                case MacroStepKind.Button:
                case MacroStepKind.ButtonDown:
                case MacroStepKind.ButtonUp:
                    PropChoice("Button", Enum.GetValues<MacroButton>(), s.Button, MacroStep.ButtonName, v => { s.Button = v; MacroEdited(); });
                    break;
                case MacroStepKind.Key:
                case MacroStepKind.KeyDown:
                case MacroStepKind.KeyUp:
                    PropKeys("Keys", s.Keys, v => { s.Keys = v; MacroEdited(); });
                    break;
                case MacroStepKind.Mouse:
                    PropChoice("Mouse button", Enum.GetValues<MacroMouse>(), s.Mouse, b => b.ToString(), v => { s.Mouse = v; MacroEdited(); });
                    break;
                case MacroStepKind.Stick:
                    PropChoice("Stick", Enum.GetValues<MacroStick>(), s.Stick, b => b.ToString(), v => { s.Stick = v; MacroEdited(); });
                    PropNumber("Left / right", -100, 100, s.X, v => { s.X = v; MacroEdited(); });
                    PropNumber("Down / up", -100, 100, s.Y, v => { s.Y = v; MacroEdited(); });
                    break;
                case MacroStepKind.Rumble:
                    PropNumber("Strength (%)", 0, 100, s.Strength, v => { s.Strength = v; MacroEdited(); });
                    break;
            }
            if (s.Timed)
                PropNumber(s.Kind == MacroStepKind.Wait ? "Wait (ms)" : s.Kind is MacroStepKind.Stick or MacroStepKind.Rumble ? "For (ms)" : "Hold for (ms)",
                    0, 600000, s.Ms, v => { s.Ms = v; MacroEdited(); });
            if (s.Kind is MacroStepKind.Button or MacroStepKind.Key or MacroStepKind.Mouse && s.Ms < 20)
                PropNote("Many games miss presses shorter than about 20 ms.");
        }

        DarkTheme.Apply(_macroProps);
        if (_macroPageScaled)
            foreach (Control c in _macroProps.Controls) ScaleToDpi(c);
        _macroProps.ResumeLayout(true);
    }

    void AddRepeatProps(Macro m)
    {
        PropHeader("How often it runs");
        PropChoice("Repeat", Enum.GetValues<MacroRepeat>(), m.Repeat, r => r switch
        {
            MacroRepeat.WhileHeld => "Again and again while held",
            MacroRepeat.Toggle => "On / off: until pressed again",
            _ => "A set number of times",
        }, v => { m.Repeat = v; MacroEdited(); ShowProps(); });
        if (m.Repeat == MacroRepeat.Once)
            PropNumber("Times", 1, 1000, m.Iterations, v => { m.Iterations = v; MacroEdited(); });
        PropNumber("Pause between (ms)", 0, 600000, m.PauseBetweenMs, v => { m.PauseBetweenMs = v; MacroEdited(); });
        if (m.Repeat == MacroRepeat.WhileHeld && m.Trigger == MacroTrigger.Release)
            PropNote("\"While held\" can't apply to a release trigger; it runs once.");
    }

    Control PropRow(string label, Control field)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        row.Controls.Add(new Label { Text = label, AutoSize = false, Size = new Size(110, 24), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 4, 3, 3) });
        row.Controls.Add(field);
        _macroProps.Controls.Add(row);
        return row;
    }

    void PropHeader(string text) => _macroProps.Controls.Add(new Label
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Font, FontStyle.Bold),
        Margin = new Padding(3, 10, 3, 4),
    });

    void PropNote(string text) => _macroProps.Controls.Add(new Label
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(300, 0),
        Tag = DarkTheme.Subtle,
        Margin = new Padding(3, 4, 3, 6),
    });

    void PropText(string label, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, Width = 170 };
        box.TextChanged += (_, _) => set(box.Text);
        PropRow(label, box);
    }

    void PropNumber(string label, int min, int max, int value, Action<int> set)
    {
        var num = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 100, Increment = max > 1000 ? 10 : 1 };
        num.ValueChanged += (_, _) => set((int)num.Value);
        PropRow(label, num);
    }

    void PropCheck(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        box.CheckedChanged += (_, _) => set(box.Checked);
        _macroProps.Controls.Add(box);
    }

    void PropChoice<T>(string label, T[] items, T current, Func<T, string> name, Action<T> set)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        foreach (var i in items) box.Items.Add(name(i));
        box.SelectedIndex = Math.Max(0, Array.IndexOf(items, current));
        box.SelectedIndexChanged += (_, _) => { if (box.SelectedIndex >= 0) BeginInvoke(() => set(items[box.SelectedIndex])); };
        PropRow(label, box);
    }

    /// <summary>A box that records the key combination pressed in it (like the Shortcuts page).</summary>
    void PropKeys(string label, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, Width = 170, PlaceholderText = "Click, then press keys" };
        string? modifiersOnly = null;
        box.PreviewKeyDown += (_, e) => e.IsInputKey = true;
        box.KeyPress += (_, e) => e.Handled = true;
        box.KeyDown += (_, e) =>
        {
            e.Handled = e.SuppressKeyPress = true;
            bool win = (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;
            bool modifier = KeyCombo.IsModifierKey(e.KeyCode);
            var text = KeyCombo.Format(e.Control, e.Shift, e.Alt, win, modifier ? Keys.None : e.KeyCode);
            if (modifier)
            {
                modifiersOnly = text;
                box.Text = text + "+…";
                return;
            }
            modifiersOnly = null;
            box.Text = text;
            set(text);
        };
        box.KeyUp += (_, e) =>
        {
            if (modifiersOnly == null || !KeyCombo.IsModifierKey(e.KeyCode)) return;
            box.Text = modifiersOnly;
            set(modifiersOnly);
            modifiersOnly = null;
        };
        PropRow(label, box);
        PropNote("Click the box and press the key or combination.");
    }
}
