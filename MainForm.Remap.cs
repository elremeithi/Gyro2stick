namespace DSGyro360;

/// <summary>The Remap page: Off, or a preset that plays a keyboard-and-mouse game with the controller.</summary>
public sealed partial class MainForm
{
    ComboBox _remapBox = new();
    ListView _remapTable = new();
    Label _remapNotes = new();
    Label _remapStatus = new();

    Control BuildRemapGroup()
    {
        _remapTable = new ListView();
        _remapNotes = new Label();
        _remapStatus = new Label();

        var group = new GroupBox
        {
            Text = "Remap: play games that have no controller support, with keys and mouse made from the controller",
            AutoSize = true,
            Padding = new Padding(4, 0, 4, 1),
        };
        var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill };

        // Left: choose a layout, then its options.
        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        left.Controls.Add(new Label { Text = "Layout", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 4) });
        var ids = new[] { "" }.Concat(RemapPresets.All.Select(p => p.Id)).ToArray();
        _remapBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, Margin = new Padding(3, 3, 3, 3) };
        _remapBox.Items.Add("Off: the controller stays an Xbox controller");
        foreach (var p in RemapPresets.All) _remapBox.Items.Add(p.Name);
        _remapBox.SelectedIndex = Math.Max(0, Array.IndexOf(ids, _pending.RemapPreset));
        _remapBox.SelectedIndexChanged += (_, _) =>
        {
            if (_remapBox.SelectedIndex < 0) return;
            _pending.RemapPreset = ids[_remapBox.SelectedIndex];
            ShowRemap();
            Changed();
        };
        left.Controls.Add(_remapBox);

        var onlyInGame = new CheckBox { Text = "Only while the game is the window in front", AutoSize = true, Checked = _pending.RemapOnlyInGame, Margin = new Padding(3, 12, 3, 3) };
        onlyInGame.CheckedChanged += (_, _) => { _pending.RemapOnlyInGame = onlyInGame.Checked; Changed(); };
        new ToolTip { AutoPopDelay = 20000 }.SetToolTip(onlyInGame,
            "On: everywhere else (desktop, other games) the controller works as usual.\n" +
            "Off: the layout is used everywhere, e.g. if the game isn't recognised.");
        var gyroAlways = new CheckBox { Text = "Gyro aims all the time (not only while LT / L2 is held)", AutoSize = true, Checked = _pending.RemapGyroAlways };
        gyroAlways.CheckedChanged += (_, _) => { _pending.RemapGyroAlways = gyroAlways.Checked; Changed(); };
        left.Controls.Add(onlyInGame);
        left.Controls.Add(gyroAlways);

        var sliders = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddSlider(sliders, 0, "Stick look speed", 25, 300, s => s.RemapLookSpeed, v => $"{v}%", v => _pending.RemapLookSpeed = v,
            "How fast the right stick turns the view.", step: 5);
        AddSlider(sliders, 1, "Gyro look speed", 25, 300, s => s.RemapGyroSpeed, v => $"{v}%", v => _pending.RemapGyroSpeed = v,
            "How far the view turns for each degree you turn the controller (also scaled by the Settings page's gyro sensitivity).", step: 5);
        foreach (Control c in sliders.Controls)
            if (c is MaterialSlider bar) bar.Width = 165;
        left.Controls.Add(sliders);

        _remapStatus.AutoSize = false; // fixed: its text arrives after the page is measured
        _remapStatus.Size = new Size(390, 44);
        _remapStatus.Margin = new Padding(3, 10, 3, 3);
        _remapStatus.Font = new Font(Font, FontStyle.Bold);
        left.Controls.Add(_remapStatus);
        _remapNotes.AutoSize = true;
        _remapNotes.MaximumSize = new Size(390, 0);
        _remapNotes.Tag = DarkTheme.Subtle;
        _remapNotes.Margin = new Padding(3, 8, 3, 3);
        left.Controls.Add(_remapNotes);
        grid.Controls.Add(left, 0, 0);

        // Right: what every button does in the chosen layout.
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty };
        right.Controls.Add(new Label { Text = "What each control does", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 4) });
        _remapTable.View = View.Details;
        _remapTable.FullRowSelect = true;
        _remapTable.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _remapTable.Size = new Size(600, 392);
        _remapTable.Columns.Add("Controller", 190);
        _remapTable.Columns.Add("In the game", 250);
        _remapTable.Columns.Add("Key sent", 136);
        right.Controls.Add(_remapTable);
        grid.Controls.Add(right, 1, 0);

        group.Controls.Add(grid);
        ShowRemap();
        return group;
    }

    void ShowRemap()
    {
        var preset = RemapPresets.Find(_pending.RemapPreset);
        _remapTable.BeginUpdate();
        _remapTable.Items.Clear();
        if (preset == null)
            _remapTable.Items.Add(new ListViewItem(new[] { "Everything", "Works as an Xbox 360 controller", "" }));
        else
            foreach (var (input, action, key) in preset.Rows())
                _remapTable.Items.Add(new ListViewItem(new[] { input, action, key }));
        _remapTable.EndUpdate();
        _remapNotes.Text = preset == null
            ? "Pick a game's layout to play it with the controller even though the game only supports keyboard and mouse. " +
              "The left stick presses the movement keys, the right stick and gyro move the mouse, and the buttons press the game's keys."
            : preset.Notes;
        RefreshRemapStatus();
    }

    void RefreshRemapStatus()
    {
        string text = _settings.RemapPreset != _pending.RemapPreset
            ? "Click Apply to use this layout."
            : RemapEngine.Status;
        if (_remapStatus.Text != text) _remapStatus.Text = text;
    }
}
