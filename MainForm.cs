using System.Drawing.Drawing2D;

namespace DSGyro360;

public sealed partial class MainForm : Form
{
    readonly AppSettings _settings;
    /// <summary>Edits made in the window; copied into the live settings when Apply is clicked.</summary>
    readonly AppSettings _pending = new();
    readonly ControllerManager _manager;
    bool _allowShow;
    bool _exiting;

    readonly CheckBox _power = new();
    readonly Label _driverStatus = new();
    readonly ListView _list = new();
    readonly NotifyIcon _tray = new();
    readonly Icon _trayOn = AppIcon.CreateTray(on: true);
    readonly Icon _trayOff = AppIcon.CreateTray(on: false);
    readonly Button _apply = new();
    readonly ToolStripMenuItem _trayToggle = new();
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 300 };

    const float FontScale = 1.05f;

    const string AntiDeadzoneTip =
        "Minimum output as soon as you move, to get past the game's own stick deadzone (like the ROG Ally). " +
        "Raise it until the smallest movement registers in game; usually 10-25%. Too high causes drift.";

    static readonly (string Text, GyroActivation Value)[] ActivationChoices =
    {
        ("Hold L2 / LT (aim)", GyroActivation.HoldL2),
        ("Hold R2 / RT", GyroActivation.HoldR2),
        ("Hold L1 / LB", GyroActivation.HoldL1),
        ("Hold R1 / RB", GyroActivation.HoldR1),
        ("Finger on touchpad", GyroActivation.TouchpadTouch),
        ("Always on", GyroActivation.Always),
    };

    static readonly (string Text, GyroCurve Value, string Tip)[] CurveChoices =
    {
        ("Linear (raw 1:1)", GyroCurve.Linear, "every movement maps directly; hard deadzone cut-off"),
        ("Balanced", GyroCurve.Balanced, "near 1:1 with jitter smoothed out; everyday and third-person games"),
        ("Precision", GyroCurve.Precision, "like Windows 'Enhance pointer precision': fine slow aim, faster turns go further"),
        ("Steady aim (sniper)", GyroCurve.Steady, "heavy smoothing, low gain when slow; holding on distant targets, tactical shooters"),
        ("Flick (fast shooters)", GyroCurve.Flick, "light smoothing, strong acceleration; quick flicks turn far, arena shooters"),
    };

    public MainForm(AppSettings settings, ControllerManager manager, PerformanceManager performance, bool startHidden)
    {
        _settings = settings;
        ProfileStore.EnsureDefault(settings);
        _manualProfile = settings.ActiveProfile;
        _pending.CopyOptionsFrom(settings);
        _manager = manager;
        _perf = performance;
        _allowShow = !startHidden;

        Text = "DS Gyro 360";
        Icon = AppIcon.Create();
        Font = new Font("Segoe UI", 9f * FontScale);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        Controls.Add(_root);
        _sections = new Sections(
            BuildHeader(), BuildTabs(), BuildList(), BuildScope(), BuildGyroGroup(), BuildControllerGroup(),
            BuildShortcutsGroup(), BuildAppGroup(), BuildPerfGroup(), BuildMacrosGroup(), BuildRemapGroup(),
            new Label
            {
                AutoSize = true,
                Tag = DarkTheme.Subtle,
                Margin = new Padding(3, 1, 3, 0),
                Text = "Cross=A  Circle=B  Square=X  Triangle=Y  Share/Touchpad click=Back  Options=Start  PS (alone)=Guide",
            });
        // Theme and DPI-scale every section once, including the ones on the tab that isn't showing.
        var s = _sections;
        foreach (var section in new[] { s.Header, s.Tabs, s.List, s.Scope, s.Gyro, s.Controller, s.Shortcuts, s.App, s.Perf, s.Macros, s.Remap, s.Footer })
        {
            DarkTheme.Apply(section);
            ScaleToDpi(section);
        }
        _shortcutRows.Tag = true; // later row rebuilds scale their own rows
        _macroPageScaled = true;
        Padding = new Padding(Px(3));
        BackColor = _root.BackColor = DarkTheme.Background;
        ForeColor = _root.ForeColor = DarkTheme.Text;
        MeasurePages();

        BuildTray();

        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Tick += (_, _) => RefreshRemapStatus();
        _refresh.Start();
        RefreshStatus();

        _autoSwitch.Tick += (_, _) => AutoSwitchTick();
        _autoSwitch.Start();
        SyncProfileUi();
    }

    // ---------- game profiles ----------

    readonly ComboBox _profileBox = new();
    readonly ToolStripMenuItem _profilesMenu = new("Profile");
    readonly System.Windows.Forms.Timer _autoSwitch = new() { Interval = 1000 };
    /// <summary>The profile picked by hand; auto-switching returns here when the game leaves the front.</summary>
    string _manualProfile;
    bool _syncingProfiles;

    Control BuildProfileRow()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(6, 1, 0, 1) };
        row.Controls.Add(new Label { Text = "Profile", AutoSize = true, Margin = new Padding(3, 6, 3, 2) });
        _profileBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _profileBox.Width = 165;
        _profileBox.Margin = new Padding(3, 3, 3, 2);
        _profileBox.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingProfiles || _profileBox.SelectedIndex < 0) return;
            ActivateProfile(_settings.Profiles[_profileBox.SelectedIndex].Name, manual: true);
        };
        new ToolTip().SetToolTip(_profileBox,
            "Each profile keeps its own gyro, controller, shortcut and performance settings. " +
            "Profiles tied to games switch automatically while that game is in front.");
        var manage = new Button { Text = "Profiles…", AutoSize = true, Margin = new Padding(2, 2, 2, 2) };
        manage.Click += (_, _) => ShowProfilesDialog();
        new ToolTip().SetToolTip(manage, "New / rename / delete profiles, tie them to games, export and import (backup / restore).");
        row.Controls.Add(_profileBox);
        row.Controls.Add(manage);
        return row;
    }

    /// <summary>Stores the options in use into the active profile.</summary>
    void SaveActiveProfile()
    {
        if (ProfileStore.Find(_settings, _settings.ActiveProfile) is { } p) p.Options = _settings.SnapshotProfile();
    }

    /// <summary>
    /// Switches to a profile: the current options are kept in the old one, the new one's options go live and the
    /// window is rebuilt to show them. <paramref name="reload"/> re-reads the active profile (after an import).
    /// </summary>
    void ActivateProfile(string name, bool manual, bool reload = false)
    {
        var target = ProfileStore.Find(_settings, name);
        if (target == null) return;
        if (manual) _manualProfile = target.Name;
        bool same = string.Equals(target.Name, _settings.ActiveProfile, StringComparison.OrdinalIgnoreCase);
        if (same && !reload)
        {
            SyncProfileUi();
            return;
        }
        if (_dirty && manual)
        {
            var answer = MessageBox.Show(this, "Apply your unapplied changes to the current profile before switching?",
                "DS Gyro 360", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
            {
                SyncProfileUi();
                return;
            }
            if (answer == DialogResult.Yes) Apply();
        }
        if (!same) SaveActiveProfile();
        _settings.CopyProfileFrom(target.Options ?? _settings.SnapshotProfile());
        _settings.ActiveProfile = target.Name;
        _settings.Save();
        _manager.SettingsChanged();
        _perf.Changed();
        _pending.CopyOptionsFrom(_settings);
        KeepOnScreen(RebuildOptionSections);
        StyleApply(dirty: false);
        SyncProfileUi();
    }

    /// <summary>Follows the game in front: its profile while it's there, the hand-picked one otherwise.</summary>
    void AutoSwitchTick()
    {
        if (!_settings.AutoSwitchProfiles || _dirty || _profilesDialogOpen) return;
        var exe = ProfileStore.ForegroundExe();
        if (exe == null) return; // our own window, or nothing: keep what's there
        var game = _settings.Profiles.FirstOrDefault(p => !p.IsDefault && p.Matches(exe));
        string want = game?.Name ?? _manualProfile;
        if (ProfileStore.Find(_settings, want) == null) want = GameProfile.DefaultName;
        if (string.Equals(want, _settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)) return;
        ActivateProfile(want, manual: false);
        _tray.ShowBalloonTip(1500, "DS Gyro 360", "Profile: " + want + (game != null ? $" ({exe})" : ""), ToolTipIcon.None);
    }

    bool _profilesDialogOpen;

    void ShowProfilesDialog()
    {
        SaveActiveProfile();
        _settings.Save();
        _profilesDialogOpen = true;
        string? use;
        bool replaced;
        using (var dlg = new ProfilesDialog(_settings, () => { SaveActiveProfile(); _settings.Save(); }))
        {
            dlg.ShowDialog(this);
            use = dlg.UseProfile;
            replaced = dlg.ActiveReplaced;
        }
        _profilesDialogOpen = false;
        ProfileStore.EnsureDefault(_settings);
        if (ProfileStore.Find(_settings, _manualProfile) == null) _manualProfile = GameProfile.DefaultName;
        if (use != null) ActivateProfile(use, manual: true, reload: replaced);
        else if (replaced) ActivateProfile(_settings.ActiveProfile, manual: false, reload: true);
        SyncProfileUi();
    }

    /// <summary>Profile picker and tray menu show the profile list and the active one.</summary>
    void SyncProfileUi()
    {
        _syncingProfiles = true;
        _profileBox.BeginUpdate();
        _profileBox.Items.Clear();
        foreach (var p in _settings.Profiles) _profileBox.Items.Add(p.Name);
        _profileBox.SelectedIndex = Math.Max(0, _settings.Profiles.FindIndex(p =>
            string.Equals(p.Name, _settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)));
        _profileBox.EndUpdate();
        _syncingProfiles = false;

        _profilesMenu.DropDownItems.Clear();
        foreach (var p in _settings.Profiles)
        {
            var name = p.Name;
            var item = new ToolStripMenuItem(name)
            {
                Checked = string.Equals(name, _settings.ActiveProfile, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => ActivateProfile(name, manual: true);
            _profilesMenu.DropDownItems.Add(item);
        }
        _profilesMenu.DropDownItems.Add(new ToolStripSeparator());
        _profilesMenu.DropDownItems.Add("Manage profiles…", null, (_, _) => { ShowFromTray(); ShowProfilesDialog(); });
        _profilesMenu.Text = "Profile: " + _settings.ActiveProfile;
        if (_tray.ContextMenuStrip != null) DarkTheme.Apply(_tray.ContextMenuStrip);
    }

    /// <summary>Recreates the option groups so they show the active profile's values.</summary>
    void RebuildOptionSections()
    {
        var old = _sections;
        _sections = old with
        {
            Gyro = BuildGyroGroup(),
            Controller = BuildControllerGroup(),
            Shortcuts = BuildShortcutsGroup(),
            App = BuildAppGroup(),
            Perf = BuildPerfGroup(),
            Macros = BuildMacrosGroup(),
            Remap = BuildRemapGroup(),
        };
        var s = _sections;
        foreach (var section in new[] { s.Gyro, s.Controller, s.Shortcuts, s.App, s.Perf, s.Macros, s.Remap })
        {
            DarkTheme.Apply(section);
            ScaleToDpi(section);
        }
        _shortcutRows.Tag = true;
        _macroPageScaled = true;
        ArrangeSections();
        foreach (var c in new[] { old.Gyro, old.Controller, old.Shortcuts, old.App, old.Perf, old.Macros, old.Remap }) c.Dispose();
    }

    // ---------- layout ----------

    /// <summary>
    /// The window declares per-monitor DPI awareness, so Windows doesn't stretch it: fonts (in points) grow with the
    /// display scaling but the fixed pixel sizes below are written for 100%. Each section is scaled once after it's
    /// built so a 150% / 200% screen gets proportionally bigger buttons, columns and sliders.
    /// </summary>
    float DpiScale => DeviceDpi / 96f;

    int Px(int px) => (int)Math.Round(px * DpiScale);

    void ScaleToDpi(Control c)
    {
        float k = DpiScale;
        if (k < 1.01f) return;
        // Self-sizing buttons already fit their (DPI-sized) text; let them shrink back after the scale.
        foreach (var b in Descendants(c).Prepend(c).OfType<Button>().Where(b => b.AutoSize))
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        c.Scale(new SizeF(k, k)); // sizes, min/max sizes and absolute table columns
        foreach (var lv in Descendants(c).Prepend(c).OfType<ListView>())
            foreach (ColumnHeader col in lv.Columns)
                if (col.Width > 0) col.Width = (int)Math.Round(col.Width * k);
        foreach (var x in Descendants(c).Prepend(c))
        {
            x.Margin = ScalePad(x.Margin, k);
            if (x is not Form) x.Padding = ScalePad(x.Padding, k);
            if (x is ComboBox cb && cb.DropDownWidth > 0) cb.DropDownWidth = (int)Math.Round(cb.DropDownWidth * k);
        }
    }

    static Padding ScalePad(Padding p, float k) => new(
        (int)Math.Round(p.Left * k), (int)Math.Round(p.Top * k), (int)Math.Round(p.Right * k), (int)Math.Round(p.Bottom * k));

    static IEnumerable<Control> Descendants(Control c) =>
        c.Controls.Cast<Control>().SelectMany(x => Descendants(x).Prepend(x));

    readonly TableLayoutPanel _root = new() { AutoSize = true, Dock = DockStyle.Fill };
    Sections _sections;
    readonly TableLayoutPanel _topBar = new() { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
    readonly Panel _pageHost = new() { AutoScroll = true, Margin = Padding.Empty };
    Size _pageSize; // the largest page: every page shows in this same size, so the window never changes size

    sealed record Sections(Control Header, Control Tabs, Control List, Control Scope, Control Gyro,
        Control Controller, Control Shortcuts, Control App, Control Perf, Control Macros, Control Remap, Control Footer);

    /// <summary>
    /// One landscape layout: the ON / Apply bar and the tabs along the top, the open page under them in a
    /// fixed-size area (the size of the largest page, so switching pages never resizes the window), the
    /// button names along the bottom. A page that grows (more shortcuts) scrolls inside its area.
    /// </summary>
    void ArrangeSections()
    {
        var s = _sections;
        _root.SuspendLayout();
        var oldPages = _pageHost.Controls.Cast<Control>().ToList();
        // Take the sections out first: disposing a page would otherwise dispose the sections it still holds.
        foreach (var page in oldPages)
        {
            foreach (var col in page.Controls.OfType<FlowLayoutPanel>().Where(c => c.Tag is LayoutColumn).ToList()) col.Controls.Clear();
            page.Controls.Clear();
        }
        _pageHost.Controls.Clear();
        _root.Controls.Clear();
        _root.ColumnStyles.Clear();
        _root.RowStyles.Clear();
        _root.ColumnCount = 1;
        _root.RowCount = 3;

        if (_topBar.ColumnStyles.Count == 0)
        {
            _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _topBar.BackColor = _pageHost.BackColor = DarkTheme.Background;
            DarkTheme.DarkScrollbars(_pageHost);
        }
        _topBar.Controls.Clear();
        s.Tabs.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _topBar.Controls.Add(s.Header, 0, 0);
        _topBar.Controls.Add(s.Tabs, 1, 0);
        _root.Controls.Add(_topBar, 0, 0);
        _root.Controls.Add(_pageHost, 0, 1);
        _root.Controls.Add(s.Footer, 0, 2);

        // Stacked groups keep their natural width; give them all the widest one's width.
        int width = new[] { s.Gyro, s.Controller, s.App }.Max(c => c.PreferredSize.Width);
        foreach (var c in new[] { s.Gyro, s.Controller, s.Shortcuts, s.App, s.Scope, s.Perf })
        {
            c.Dock = DockStyle.None;
            c.Width = width;
            c.MinimumSize = new Size(width, 0);
        }
        s.Scope.Height = Px(176);
        _list.Width = width;

        var content = BuildPage(_page);
        _pageHost.Controls.Add(content);
        var natural = content.GetPreferredSize(Size.Empty);
        _pageHost.Size = _pageSize.IsEmpty ? natural : _pageSize;
        _root.ResumeLayout(true);
        foreach (var c in oldPages) c.Dispose(); // their sections have moved to the new page
    }

    /// <summary>The sections of one page, as columns side by side.</summary>
    Control BuildPage(Page page)
    {
        var s = _sections;
        FlowLayoutPanel Column(params Control[] items)
        {
            var col = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Margin = Padding.Empty,
                BackColor = DarkTheme.Background,
                Tag = LayoutColumn,
            };
            col.Controls.AddRange(items);
            return col;
        }
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = DarkTheme.Background };
        row.Controls.AddRange(page switch
        {
            Page.Macros => new[] { s.Macros },
            Page.Remap => new[] { s.Remap },
            Page.Performance => new Control[] { Column(s.Perf), Column(s.List) },
            Page.Shortcuts => new Control[] { Column(s.List, s.Shortcuts), Column(s.Scope) },
            _ => new Control[] { Column(s.List, s.Gyro, s.App), Column(s.Scope, s.Controller) },
        });
        return row;
    }

    /// <summary>Lays out every page once and keeps the largest size for all of them, trimmed to fit the screen.</summary>
    void MeasurePages()
    {
        var shown = _page;
        _pageSize = Size.Empty;
        int w = 0, h = 0;
        foreach (var page in Enum.GetValues<Page>())
        {
            _page = page;
            ArrangeSections();
            var size = _pageHost.Controls[0].GetPreferredSize(Size.Empty);
            w = Math.Max(w, size.Width);
            h = Math.Max(h, size.Height);
        }
        int bar = _topBar.GetPreferredSize(Size.Empty).Width;
        _pageSize = new Size(Math.Max(w, bar), h);
        _page = shown;
        ArrangeSections();
        // On a small screen, keep the window on it: the page area scrolls instead.
        var area = Screen.FromControl(this).WorkingArea;
        int extraW = Width - area.Width, extraH = Height - area.Height;
        if (extraW > 0 || extraH > 0)
        {
            _pageSize = new Size(_pageSize.Width - Math.Max(0, extraW),
                _pageSize.Height - Math.Max(0, extraH));
            ArrangeSections();
        }
    }

    const string LayoutColumn = "layout:column";

    /// <summary>Runs a change that resizes the window, then keeps it on screen, centred where it was.</summary>
    void KeepOnScreen(Action resize)
    {
        var center = new Point(Left + Width / 2, Top + Height / 2);
        resize();
        var area = Screen.FromControl(this).WorkingArea;
        Location = new Point(
            Math.Clamp(center.X - Width / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(center.Y - Height / 2, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    Control BuildHeader()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
        _power.Appearance = Appearance.Button;
        _power.Size = new Size(90, 32);
        _power.TextAlign = ContentAlignment.MiddleCenter;
        _power.Font = new Font("Segoe UI", 10.5f * FontScale, FontStyle.Bold);
        _power.FlatStyle = FlatStyle.Flat;
        _power.Tag = DarkTheme.KeepColors;
        _power.Checked = _settings.Enabled;
        _power.CheckedChanged += (_, _) => SetEnabled(_power.Checked);
        StylePower();
        row.Controls.Add(_power);

        _apply.Size = new Size(104, 32);
        _apply.Font = new Font("Segoe UI", 10f * FontScale, FontStyle.Bold);
        _apply.FlatStyle = FlatStyle.Flat;
        _apply.FlatAppearance.BorderSize = 0;
        _apply.Tag = DarkTheme.KeepColors;
        _apply.Margin = new Padding(4, 2, 2, 2);
        _apply.Click += (_, _) => Apply();
        StyleApply(dirty: false);
        row.Controls.Add(_apply);

        _driverStatus.AutoSize = true;
        _driverStatus.Margin = new Padding(8, 2, 2, 2);
        _driverStatus.Tag = DarkTheme.Subtle;
        row.Controls.Add(_driverStatus);
        return row;
    }

    Control BuildList()
    {
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.Size = new Size(560, 92);
        _list.Columns.Add("Xbox slot", 80);
        _list.Columns.Add("Controller", 90);
        _list.Columns.Add("Connection", 90);
        _list.Columns.Add("Battery", 62);
        _list.Columns.Add("Gyro", 72);
        _list.Columns.Add("Status", 185);
        // Last column fills the rest so no unpainted header strip shows on the right.
        _list.Resize += (_, _) => _list.Columns[^1].Width = -2;
        _list.HandleCreated += (_, _) => _list.Columns[^1].Width = -2;
        return _list;
    }

    /// <summary>Live right-stick output of the controller selected in the list (or the first one).</summary>
    Control BuildScope() => new GyroScope(() =>
    {
        var pads = _manager.Snapshot();
        int i = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : 0;
        return i < pads.Count ? pads[i].Live : pads.Select(p => p.Live).FirstOrDefault(l => l != null);
    })
    {
        Dock = DockStyle.Fill,
        MinimumSize = new Size(240, 0),
        Margin = new Padding(8, 3, 3, 3),
    };

    Control BuildGyroGroup()
    {
        var (group, grid) = Group("Gyro → right stick (added to the physical stick)");

        var enabled = new CheckBox { Text = "Gyro enabled", AutoSize = true, Checked = _pending.GyroEnabled };
        enabled.CheckedChanged += (_, _) => { _pending.GyroEnabled = enabled.Checked; Changed(); };
        grid.Controls.Add(enabled, 0, 0);

        var activation = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        foreach (var c in ActivationChoices) activation.Items.Add(c.Text);
        activation.SelectedIndex = Math.Max(0, Array.FindIndex(ActivationChoices, c => c.Value == _pending.Activation));
        activation.SelectedIndexChanged += (_, _) => { _pending.Activation = ActivationChoices[activation.SelectedIndex].Value; Changed(); };
        var actRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        actRow.Controls.Add(new Label { Text = "Activate:", AutoSize = true, Margin = new Padding(3, 5, 3, 2) });
        actRow.Controls.Add(activation);
        grid.Controls.Add(actRow, 1, 0);
        grid.SetColumnSpan(actRow, 2);

        AddSlider(grid, 1, "Sensitivity", 1, 100, s => (int)Math.Round(s.GyroSensitivity * 10),
            v => $"{v / 10.0:0.0}x", v => _pending.GyroSensitivity = v / 10.0);
        const string ScaleTip = "Scales this axis on top of Sensitivity, e.g. 60% vertical for steadier up/down aim.";
        AddSlider(grid, 2, "Horizontal (X)", 10, 200, s => (int)Math.Round(s.GyroScaleX * 100),
            v => $"{v}%", v => _pending.GyroScaleX = v / 100.0, ScaleTip, step: 5);
        AddSlider(grid, 3, "Vertical (Y)", 10, 200, s => (int)Math.Round(s.GyroScaleY * 100),
            v => $"{v}%", v => _pending.GyroScaleY = v / 100.0, ScaleTip, step: 5);
        AddSlider(grid, 4, "Deadzone", 0, 100, s => (int)Math.Round(s.GyroDeadzone * 10),
            v => $"{v / 10.0:0.0}°/s", v => _pending.GyroDeadzone = v / 10.0);
        AddSlider(grid, 5, "Gyro anti-deadzone", 0, 50, s => (int)Math.Round(s.GyroAntiDeadzone * 100),
            v => $"{v}%", v => _pending.GyroAntiDeadzone = v / 100.0,
            AntiDeadzoneTip, step: 5);

        var invRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var invX = new CheckBox { Text = "Invert X", AutoSize = true, Checked = _pending.InvertGyroX };
        invX.CheckedChanged += (_, _) => { _pending.InvertGyroX = invX.Checked; Changed(); };
        var invY = new CheckBox { Text = "Invert Y", AutoSize = true, Checked = _pending.InvertGyroY };
        invY.CheckedChanged += (_, _) => { _pending.InvertGyroY = invY.Checked; Changed(); };
        var curve = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 132, DropDownWidth = 200 };
        foreach (var c in CurveChoices) curve.Items.Add(c.Text);
        curve.SelectedIndex = Math.Max(0, Array.FindIndex(CurveChoices, c => c.Value == _pending.GyroCurve));
        curve.SelectedIndexChanged += (_, _) => { _pending.GyroCurve = CurveChoices[curve.SelectedIndex].Value; Changed(); };
        new ToolTip { AutoPopDelay = 30000 }.SetToolTip(curve, string.Join("\n", CurveChoices.Select(c => $"{c.Text}: {c.Tip}")) +
            "\n\nThe non-linear curves keep slow movements aiming instead of stopping; they work best with some Gyro anti-deadzone.");
        invRow.Controls.Add(new Label { Text = "Curve:", AutoSize = true, Margin = new Padding(3, 5, 3, 2) });
        invRow.Controls.Add(curve);
        invRow.Controls.Add(invX);
        invRow.Controls.Add(invY);
        grid.Controls.Add(invRow, 0, 6);
        grid.SetColumnSpan(invRow, 2);

        var recal = new Button { Text = "Recalibrate gyro", AutoSize = true };
        recal.Click += (_, _) =>
        {
            _manager.RecalibrateAll();
            _tray.ShowBalloonTip(2000, "DS Gyro 360", "Calibrating gyro - put the controllers down and keep them still for 1 second.", ToolTipIcon.Info);
        };
        grid.Controls.Add(recal, 2, 6);

        var creep = new CheckBox { Text = "Stop aim creep when holding still", AutoSize = true, Checked = _pending.StopRestingCreep };
        creep.CheckedChanged += (_, _) => { _pending.StopRestingCreep = creep.Checked; Changed(); };
        new ToolTip().SetToolTip(creep,
            "For the non-linear curves: Gyro anti-deadzone only kicks in once you really move (above the Deadzone), " +
            "so sensor noise on a still controller doesn't slowly push the aim. Off = anti-deadzone fades in earlier, " +
            "for the most responsive tiny movements.");
        grid.Controls.Add(creep, 0, 7);
        grid.SetColumnSpan(creep, 2);
        return group;
    }

    Control BuildControllerGroup()
    {
        var (group, grid) = Group("Controller");
        AddSlider(grid, 0, "Stick deadzone", 0, 30, s => (int)Math.Round(s.StickDeadzone * 100),
            v => $"{v}%", v => _pending.StickDeadzone = v / 100.0);
        AddSlider(grid, 1, "Left anti-deadzone", 0, 50, s => (int)Math.Round(s.LeftStickAntiDeadzone * 100),
            v => $"{v}%", v => _pending.LeftStickAntiDeadzone = v / 100.0, AntiDeadzoneTip, step: 5);
        AddSlider(grid, 2, "Right anti-deadzone", 0, 50, s => (int)Math.Round(s.RightStickAntiDeadzone * 100),
            v => $"{v}%", v => _pending.RightStickAntiDeadzone = v / 100.0, AntiDeadzoneTip, step: 5);

        var rumble = new CheckBox { Text = "Game rumble", AutoSize = true, Checked = _pending.Rumble };
        rumble.CheckedChanged += (_, _) => { _pending.Rumble = rumble.Checked; Changed(); };
        grid.Controls.Add(rumble, 0, 3);

        var hide = new CheckBox
        {
            Text = "Hide real controllers from games (HidHide)",
            AutoSize = true,
            Checked = _pending.HideControllers,
        };
        hide.CheckedChanged += (_, _) => { _pending.HideControllers = hide.Checked; Changed(); };
        grid.Controls.Add(hide, 1, 3);
        grid.SetColumnSpan(hide, 2);

        AddSlider(grid, 4, "Rumble strength", 0, 100, s => (int)Math.Round(s.RumbleStrength * 100),
            v => $"{v}%", v => _pending.RumbleStrength = v / 100.0, step: 5);

        var fix = new CheckBox
        {
            Text = "Stop rumble from moving gyro aim",
            AutoSize = true,
            Checked = _pending.RumbleGyroFix,
        };
        fix.CheckedChanged += (_, _) => { _pending.RumbleGyroFix = fix.Checked; Changed(); };
        new ToolTip().SetToolTip(fix,
            "While the motors run, cancels the false turn they cause in the gyro and smooths the shaking.");
        grid.Controls.Add(fix, 0, 5);
        grid.SetColumnSpan(fix, 2);

        var calRumble = new Button { Text = "Calibrate rumble drift", AutoSize = true };
        calRumble.Click += (_, _) =>
        {
            if (!_settings.Enabled)
            {
                MessageBox.Show(this, "Turn the app ON first.", "DS Gyro 360", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var ok = MessageBox.Show(this,
                "Put the controller(s) flat on a table and don't touch them.\n\n" +
                "They will vibrate at different strengths for about 13 seconds while the gyro drift is measured. " +
                "Watch the Status column; the result is saved automatically.",
                "Calibrate rumble drift", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (ok == DialogResult.OK) _manager.CalibrateRumbleAll();
        };
        grid.Controls.Add(calRumble, 2, 5);
        AddBuiltInRows(grid, 6);
        return group;
    }

    static readonly (string Text, BuiltInMode Value)[] BuiltInChoices =
    {
        ("Auto (GPD Win 4 / handhelds)", BuiltInMode.Auto),
        ("On", BuiltInMode.On),
        ("Off", BuiltInMode.Off),
    };

    static readonly string[] AxisNames = { "+X", "−X", "+Y", "−Y", "+Z", "−Z" };

    /// <summary>Handheld PCs: use the built-in Xbox-style controller with the built-in gyro, and which sensor axes to use.</summary>
    void AddBuiltInRows(TableLayoutPanel grid, int row)
    {
        var tt = new ToolTip { AutoPopDelay = 30000 };
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 235 };
        foreach (var c in BuiltInChoices) mode.Items.Add(c.Text);
        mode.SelectedIndex = Math.Max(0, Array.FindIndex(BuiltInChoices, c => c.Value == _pending.BuiltInController));
        mode.SelectedIndexChanged += (_, _) => { _pending.BuiltInController = BuiltInChoices[mode.SelectedIndex].Value; Changed(); };
        const string ModeTip =
            "Handheld PCs such as the GPD Win 4: the built-in controller is replaced by a virtual Xbox 360 pad with the " +
            "built-in gyro added to the right stick, and hidden from games (HidHide).\n" +
            "Auto turns this on only on GPD / AYANEO / ONEXPLAYER devices. The gyro needs the device's motion-sensor " +
            "driver (GPD: BMI260 driver from GPD's support site).";
        var modeLabel = new Label { Text = "Built-in controller", AutoSize = true, Margin = new Padding(3, 5, 3, 2) };
        tt.SetToolTip(modeLabel, ModeTip);
        tt.SetToolTip(mode, ModeTip);
        grid.Controls.Add(modeLabel, 0, row);
        grid.Controls.Add(mode, 1, row);

        ComboBox AxisBox(SensorAxis value, Action<SensorAxis> set)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 58 };
            box.Items.AddRange(AxisNames);
            box.SelectedIndex = (int)value;
            box.SelectedIndexChanged += (_, _) => { set((SensorAxis)box.SelectedIndex); Changed(); };
            return box;
        }
        var turn = AxisBox(_pending.BuiltInTurnAxis, v => _pending.BuiltInTurnAxis = v);
        var tilt = AxisBox(_pending.BuiltInTiltAxis, v => _pending.BuiltInTiltAxis = v);
        var axes = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        axes.Controls.Add(new Label { Text = "Turn", AutoSize = true, Margin = new Padding(3, 5, 3, 2) });
        axes.Controls.Add(turn);
        axes.Controls.Add(new Label { Text = "Tilt", AutoSize = true, Margin = new Padding(6, 5, 3, 2) });
        axes.Controls.Add(tilt);
        const string AxesTip =
            "Which axis of the built-in motion sensor turns the aim left/right (Turn) and up/down (Tilt). " +
            "If aiming goes the wrong way or on the wrong axis, use Detect axes.";
        tt.SetToolTip(turn, AxesTip);
        tt.SetToolTip(tilt, AxesTip);
        var axesLabel = new Label { Text = "Built-in gyro axes", AutoSize = true, Margin = new Padding(3, 5, 3, 2) };
        tt.SetToolTip(axesLabel, AxesTip);
        grid.Controls.Add(axesLabel, 0, row + 1);
        grid.Controls.Add(axes, 1, row + 1);

        var detect = new Button { Text = "Detect axes…", AutoSize = true };
        detect.Click += (_, _) =>
        {
            var gyro = _manager.Gyro;
            if (gyro == null)
            {
                MessageBox.Show(this,
                    "No built-in motion sensor is in use.\n\nSet Built-in controller to Auto or On and click Apply. " +
                    "If it still isn't found, install the device's gyro driver (GPD Win 4: the BMI260 driver from GPD).",
                    "Detect axes", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var wizard = new AxisWizard(gyro);
            if (wizard.ShowDialog(this) != DialogResult.OK) return;
            turn.SelectedIndex = (int)wizard.Turn; // the boxes' handlers update the pending settings
            tilt.SelectedIndex = (int)wizard.Tilt;
            Changed();
        };
        grid.Controls.Add(detect, 2, row + 1);
    }

    static readonly (string Text, ChordButton Value)[] ChordChoices =
    {
        ("Create (Share)", ChordButton.Create),
        ("Options", ChordButton.Options),
        ("Touchpad click", ChordButton.TouchpadClick),
        ("Triangle", ChordButton.Triangle),
        ("Circle", ChordButton.Circle),
        ("Cross", ChordButton.Cross),
        ("Square", ChordButton.Square),
        ("L1", ChordButton.L1),
        ("R1", ChordButton.R1),
        ("L2", ChordButton.L2),
        ("R2", ChordButton.R2),
        ("L3 (left stick click)", ChordButton.L3),
        ("R3 (right stick click)", ChordButton.R3),
        ("D-pad up", ChordButton.DpadUp),
        ("D-pad down", ChordButton.DpadDown),
        ("D-pad left", ChordButton.DpadLeft),
        ("D-pad right", ChordButton.DpadRight),
        ("None", ChordButton.None),
    };

    // ---------- shortcuts page ----------

    TableLayoutPanel _shortcutRows = new();

    Control BuildShortcutsGroup()
    {
        _shortcutRows = new TableLayoutPanel { ColumnCount = 5, AutoSize = true, Margin = Padding.Empty };
        var group = new GroupBox
        {
            Text = "Shortcuts: PS + button (the game doesn't see it)",
            AutoSize = true,
            Padding = new Padding(4, 0, 4, 1),
        };
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        stack.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Tag = DarkTheme.Subtle,
            Margin = new Padding(3, 1, 3, 3),
            Text = "Hold PS and press the button. Keys like Alt stay held while PS is held, so for Alt+Tab: " +
                   "hold PS, tap the button to step through windows, release PS to switch.",
        });
        stack.Controls.Add(_shortcutRows);
        var add = new Button { Text = "+  Add shortcut", AutoSize = true, Margin = new Padding(3, 3, 3, 2) };
        add.Click += (_, _) =>
        {
            var used = _pending.Shortcuts.Select(s => s.Button).ToHashSet();
            var free = ChordChoices.Select(c => c.Value).FirstOrDefault(b => b != ChordButton.None && !used.Contains(b));
            _pending.Shortcuts.Add(new Shortcut { Button = free == ChordButton.None ? ChordButton.Cross : free });
            RebuildShortcutRows();
            Changed();
        };
        stack.Controls.Add(add);
        group.Controls.Add(stack);
        RebuildShortcutRows();
        return group;
    }

    void RebuildShortcutRows()
    {
        _shortcutRows.SuspendLayout();
        foreach (var c in _shortcutRows.Controls.Cast<Control>().ToList()) c.Dispose();
        _shortcutRows.Controls.Clear();
        _shortcutRows.RowStyles.Clear();
        _shortcutRows.RowCount = _pending.Shortcuts.Count;
        for (int i = 0; i < _pending.Shortcuts.Count; i++) AddShortcutRow(i, _pending.Shortcuts[i]);
        if (_pending.Shortcuts.Count == 0)
            _shortcutRows.Controls.Add(new Label { Text = "No shortcuts yet.", AutoSize = true, Tag = DarkTheme.Subtle }, 0, 0);
        DarkTheme.Apply(_shortcutRows);
        // Rows rebuilt after the page was DPI-scaled need scaling themselves.
        if (_shortcutRows.Tag is true)
            foreach (Control c in _shortcutRows.Controls) ScaleToDpi(c);
        _shortcutRows.ResumeLayout(true);
    }

    void AddShortcutRow(int row, Shortcut sc)
    {
        var buttons = ChordChoices.Where(c => c.Value != ChordButton.None).ToArray();
        var presets = ShortcutPresets.All;
        int customIndex = presets.Length, runIndex = presets.Length + 1;

        var button = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
        foreach (var c in buttons) button.Items.Add("PS + " + c.Text);
        button.SelectedIndex = Math.Max(0, Array.FindIndex(buttons, c => c.Value == sc.Button));
        button.SelectedIndexChanged += (_, _) => { sc.Button = buttons[button.SelectedIndex].Value; Changed(); };

        var action = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175, DropDownWidth = 240 };
        foreach (var p in presets) action.Items.Add(p.Text);
        action.Items.Add("Pick key or button…");
        action.Items.Add("Run program…");

        // Third column: the binding (opens the keyboard / mouse / gamepad picker), or the program box for Run.
        var cell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var bind = new Button { Size = new Size(150, 24), Margin = new Padding(2, 1, 2, 1), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        var param = new TextBox { Width = 150, Margin = new Padding(3, 4, 3, 3), PlaceholderText = "Program, file or web address" };
        cell.Controls.Add(bind);
        cell.Controls.Add(param);
        var browse = new Button { Text = "…", Size = new Size(26, 24), Margin = new Padding(1) };
        var remove = new Button { Text = "✕", Size = new Size(26, 24), Margin = new Padding(1) };
        var tt = new ToolTip();
        tt.SetToolTip(bind, "Click to pick a key, mouse button or gamepad button");
        tt.SetToolTip(browse, "Choose a program or file");
        tt.SetToolTip(remove, "Remove this shortcut");

        bool syncing = false;
        void Sync()
        {
            syncing = true;
            int preset = sc.Kind == ShortcutKind.Keys
                ? Array.FindIndex(presets, p => string.Equals(p.Keys, sc.Keys, StringComparison.OrdinalIgnoreCase)) : -1;
            action.SelectedIndex = sc.Kind == ShortcutKind.Run ? runIndex : preset >= 0 ? preset : customIndex;
            bool run = sc.Kind == ShortcutKind.Run;
            bind.Visible = !run;
            param.Visible = browse.Visible = run;
            bind.Text = (sc.Kind switch { ShortcutKind.Mouse => "🖱  ", ShortcutKind.Pad => "🎮  ", _ => "⌨  " }) + sc.BindingText;
            if (run && param.Text != sc.Target) param.Text = sc.Target;
            syncing = false;
        }

        void Pick()
        {
            using var picker = new BindingPicker(sc);
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                sc.Kind = picker.Kind;
                sc.Keys = picker.Keys;
                sc.Mouse = picker.Mouse;
                sc.Pad = picker.Pad;
                Changed();
            }
            Sync();
        }
        bind.Click += (_, _) => Pick();

        action.SelectedIndexChanged += (_, _) =>
        {
            if (syncing) return;
            int i = action.SelectedIndex;
            if (i == runIndex) sc.Kind = ShortcutKind.Run;
            else if (i < presets.Length)
            {
                sc.Kind = ShortcutKind.Keys;
                sc.Keys = presets[i].Keys;
            }
            else
            {
                BeginInvoke(Pick); // after the drop-down has closed
                return;
            }
            Sync();
            Changed();
        };

        param.TextChanged += (_, _) =>
        {
            if (syncing || sc.Kind != ShortcutKind.Run) return;
            sc.Target = param.Text;
            Changed();
        };

        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Choose a program or file",
                Filter = "Programs and shortcuts (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            sc.Target = dlg.FileName;
            Sync();
            Changed();
        };
        remove.Click += (_, _) =>
        {
            _pending.Shortcuts.Remove(sc);
            BeginInvoke(RebuildShortcutRows); // not while this button's click is still running
            Changed();
        };

        Sync();
        _shortcutRows.Controls.Add(button, 0, row);
        _shortcutRows.Controls.Add(action, 1, row);
        _shortcutRows.Controls.Add(cell, 2, row);
        _shortcutRows.Controls.Add(browse, 3, row);
        _shortcutRows.Controls.Add(remove, 4, row);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern short GetKeyState(int key);

    // ---------- tabs ----------

    enum Page { Settings, Shortcuts, Macros, Remap, Performance }
    Page _page;
    readonly Button _tabSettings = new(), _tabShortcuts = new(), _tabMacros = new(), _tabRemap = new(), _tabPerf = new();

    Control BuildTabs()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        foreach (var (b, text, page) in new[] { (_tabSettings, "Settings", Page.Settings), (_tabShortcuts, "Shortcuts", Page.Shortcuts),
                     (_tabMacros, "Macros", Page.Macros), (_tabRemap, "Remap", Page.Remap), (_tabPerf, "Performance", Page.Performance) })
        {
            b.Text = text;
            b.Size = new Size(90, 28);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Tag = DarkTheme.KeepColors;
            b.Margin = new Padding(1, 2, 0, 1);
            b.Click += (_, _) => ShowPage(page);
            row.Controls.Add(b);
        }
        row.Controls.Add(BuildProfileRow());
        StyleTabs();
        return row;
    }

    void StyleTabs()
    {
        foreach (var (b, page) in new[] { (_tabSettings, Page.Settings), (_tabShortcuts, Page.Shortcuts), (_tabMacros, Page.Macros), (_tabRemap, Page.Remap), (_tabPerf, Page.Performance) })
        {
            bool selected = _page == page;
            DarkTheme.SetTab(b, selected);
        }
    }

    void ShowPage(Page page)
    {
        if (_page == page) return;
        _page = page;
        StyleTabs();
        KeepOnScreen(ArrangeSections);
    }

    Control BuildAppGroup()
    {
        var (group, grid) = Group("App");
        var autostart = new CheckBox { Text = "Start with Windows", AutoSize = true, Checked = _pending.StartWithWindows };
        autostart.CheckedChanged += (_, _) =>
        {
            _pending.StartWithWindows = autostart.Checked;
            Changed();
        };
        var minimized = new CheckBox { Text = "Start minimized to tray", AutoSize = true, Checked = _pending.StartMinimized };
        minimized.CheckedChanged += (_, _) => { _pending.StartMinimized = minimized.Checked; Changed(); };
        var closeToTray = new CheckBox { Text = "Close button hides to tray", AutoSize = true, Checked = _pending.CloseToTray };
        closeToTray.CheckedChanged += (_, _) => { _pending.CloseToTray = closeToTray.Checked; Changed(); };
        grid.Controls.Add(autostart, 0, 0);
        grid.Controls.Add(minimized, 1, 0);
        grid.Controls.Add(closeToTray, 2, 0);
        return group;
    }

    // ---------- performance page ----------

    readonly PerformanceManager _perf;
    Label _perfLive = new(), _perfStatus = new();
    readonly List<(Button Button, PerfProfile Profile)> _profileButtons = new();
    readonly List<(ToolStripMenuItem Item, PerfProfile Profile)> _profileMenu = new();
    Button _installPawnIo = new(), _installRtss = new();
    MaterialSlider? _fanFixedBar;
    int _pawnIoCheck;

    static readonly (string Text, PerfProfile Value)[] ProfileChoices =
    {
        ("Off", PerfProfile.Off),
        ("30 FPS", PerfProfile.Fps30),
        ("45 FPS", PerfProfile.Fps45),
        ("60 FPS", PerfProfile.Fps60),
    };

    static readonly (string Text, FanMode Value)[] FanChoices =
    {
        ("Curve (follows the FPS profile)", FanMode.Curve),
        ("Automatic (GPD default)", FanMode.Auto),
        ("Fixed speed", FanMode.Fixed),
    };

    Control BuildPerfGroup()
    {
        var (group, grid) = Group("Performance: power limit, frame cap and fan (GPD Win 4 6800U)");
        // Fresh controls each build (the page is rebuilt when the profile changes).
        _profileButtons.Clear();
        _perfLive = new Label();
        _perfStatus = new Label();
        _installPawnIo = new Button();
        _installRtss = new Button();
        _pawnIoCheck = 0;

        var profiles = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
        foreach (var (text, value) in ProfileChoices)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(96, 38),
                FlatStyle = FlatStyle.Flat,
                Tag = DarkTheme.KeepColors,
                Font = new Font("Segoe UI", 11f * FontScale, FontStyle.Bold),
                Margin = new Padding(2, 2, 4, 2),
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => SetProfile(value);
            _profileButtons.Add((b, value));
            profiles.Controls.Add(b);
        }
        grid.Controls.Add(profiles, 0, 0);
        grid.SetColumnSpan(profiles, 3);

        const string WattTip = "Power limit (sustained and boost) the processor is held to with this profile. " +
                               "The GPD Win 4 6800U runs from about 5 W to 28 W; lower is cooler, quieter and lasts longer.";
        AddSlider(grid, 1, "30 FPS power", 5, 30, s => s.TdpFps30, v => $"{v} W", v => _settings.TdpFps30 = v, WattTip,
            source: _settings, onChange: PerfChanged);
        AddSlider(grid, 2, "45 FPS power", 5, 30, s => s.TdpFps45, v => $"{v} W", v => _settings.TdpFps45 = v, WattTip,
            source: _settings, onChange: PerfChanged);
        AddSlider(grid, 3, "60 FPS power", 5, 30, s => s.TdpFps60, v => $"{v} W", v => _settings.TdpFps60 = v, WattTip,
            source: _settings, onChange: PerfChanged);

        var cap = new CheckBox { Text = "Also cap the frame rate (RivaTuner Statistics Server)", AutoSize = true, Checked = _settings.CapFps };
        cap.CheckedChanged += (_, _) => { _settings.CapFps = cap.Checked; PerfChanged(); };
        new ToolTip().SetToolTip(cap,
            "Sets RTSS's global frame-rate limit to 30 / 45 / 60 and starts RTSS if it isn't running. " +
            "Needs RivaTuner Statistics Server (free, from Guru3D). Off removes the cap.");
        grid.Controls.Add(cap, 0, 4);
        grid.SetColumnSpan(cap, 3);

        var fan = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 235 };
        foreach (var c in FanChoices) fan.Items.Add(c.Text);
        fan.SelectedIndex = Math.Max(0, Array.FindIndex(FanChoices, c => c.Value == _settings.FanMode));
        fan.SelectedIndexChanged += (_, _) =>
        {
            _settings.FanMode = FanChoices[fan.SelectedIndex].Value;
            if (_fanFixedBar != null) _fanFixedBar.Enabled = _settings.FanMode == FanMode.Fixed;
            PerfChanged();
        };
        var fanLabel = new Label { Text = "Fan", AutoSize = true, Margin = new Padding(3, 5, 3, 2) };
        new ToolTip { AutoPopDelay = 30000 }.SetToolTip(fan,
            "Curve: fan speed follows the CPU temperature, quieter on 30 FPS and stronger on 60 FPS (Off profile = GPD automatic).\n" +
            "Automatic: the GPD's own fan control.\nFixed speed: the speed set below.\n" +
            "Above 95 °C the fan always runs at full speed, and it goes back to automatic when the app exits.");
        grid.Controls.Add(fanLabel, 0, 5);
        grid.Controls.Add(fan, 1, 5);
        AddSlider(grid, 6, "Fixed fan speed", 0, 100, s => s.FanFixedPercent, v => $"{v}%", v => _settings.FanFixedPercent = v,
            step: 5, source: _settings, onChange: PerfChanged);
        _fanFixedBar = grid.GetControlFromPosition(1, 6) as MaterialSlider;
        if (_fanFixedBar != null) _fanFixedBar.Enabled = _settings.FanMode == FanMode.Fixed;

        _perfLive.AutoSize = true;
        _perfLive.Font = new Font("Segoe UI", 10.5f * FontScale, FontStyle.Bold);
        _perfLive.Margin = new Padding(3, 4, 3, 2);
        grid.Controls.Add(_perfLive, 0, 7);
        grid.SetColumnSpan(_perfLive, 3);

        _perfStatus.AutoSize = true;
        _perfStatus.MaximumSize = new Size(520, 0);
        _perfStatus.Tag = DarkTheme.Subtle;
        _perfStatus.Margin = new Padding(3, 2, 3, 2);
        grid.Controls.Add(_perfStatus, 0, 8);
        grid.SetColumnSpan(_perfStatus, 3);

        _installPawnIo.Text = "Install PawnIO driver…";
        _installPawnIo.AutoSize = true;
        _installPawnIo.Visible = false;
        _installPawnIo.Click += async (_, _) =>
        {
            var ok = MessageBox.Show(this,
                "Power limits and fan control need PawnIO, a signed hardware-access driver (by namazso, pawnio.eu) that " +
                "replaces the old WinRing0 driver Windows Defender now blocks.\n\nInstall it now? It is bundled with this app.",
                "Install PawnIO", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ok != DialogResult.OK) return;
            _installPawnIo.Enabled = false;
            _installPawnIo.Text = "Installing…";
            var error = await Task.Run(PawnIoSetup.Install);
            _installPawnIo.Enabled = true;
            _installPawnIo.Text = "Install PawnIO driver…";
            _pawnIoCheck = 0;
            _perf.Changed();
            MessageBox.Show(this, error == null ? "PawnIO is installed." : "PawnIO could not be installed:\n\n" + error,
                "Install PawnIO", MessageBoxButtons.OK, error == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        };
        var installRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        installRow.Controls.Add(_installPawnIo);

        _installRtss.Text = "Install RivaTuner…";
        _installRtss.AutoSize = true;
        _installRtss.Visible = false;
        _installRtss.Click += async (_, _) =>
        {
            var setup = RtssSetup.FindLocal();
            if (setup == null)
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath);
                MessageBox.Show(this,
                    "The frame cap uses RivaTuner Statistics Server (free, from guru3d.com), which can't be bundled with this app.\n\n" +
                    $"Copy its installer (RTSSSetup….exe, or the downloaded .zip) into:\n{dir}\n\nthen click this button again. " +
                    "Power limits and fan control work without it.",
                    "Install RivaTuner", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _installRtss.Enabled = false;
            _installRtss.Text = "Installing…";
            var error = await Task.Run(() => RtssSetup.Install(setup));
            _installRtss.Enabled = true;
            _installRtss.Text = "Install RivaTuner…";
            _pawnIoCheck = 0;
            _perf.Changed(); // apply the cap now that RTSS is there
            if (error != null)
                MessageBox.Show(this, "RivaTuner could not be installed:\n\n" + error, "Install RivaTuner",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        new ToolTip().SetToolTip(_installRtss,
            "Runs a RivaTuner installer (RTSSSetup….exe or its .zip) copied next to DSGyro360.exe - works offline.");
        installRow.Controls.Add(_installRtss);
        grid.Controls.Add(installRow, 0, 9);
        grid.SetColumnSpan(installRow, 3);

        StyleProfiles();
        return group;
    }

    void SetProfile(PerfProfile p)
    {
        _settings.PerfProfile = p;
        PerfChanged();
    }

    /// <summary>Performance options apply and save instantly (no Apply button), like a handheld's quick settings.</summary>
    void PerfChanged()
    {
        SaveActiveProfile();
        _settings.Save();
        _perf.Changed();
        StyleProfiles();
    }

    void StyleProfiles()
    {
        foreach (var (b, p) in _profileButtons)
        {
            bool selected = _settings.PerfProfile == p;
            b.BackColor = selected ? DarkTheme.Primary : DarkTheme.Surface;
            b.ForeColor = selected ? DarkTheme.OnPrimary : DarkTheme.Text;
            b.Invalidate();
            int w = PerformanceManager.WattsFor(_settings, p);
            b.Text = p == PerfProfile.Off ? "Off" : $"{ProfileChoices.First(c => c.Value == p).Text}\n{w} W";
            b.Font = new Font("Segoe UI", (p == PerfProfile.Off ? 11f : 9.5f) * FontScale, FontStyle.Bold);
        }
        foreach (var (item, p) in _profileMenu) item.Checked = _settings.PerfProfile == p;
    }

    void RefreshPerformance()
    {
        if (--_pawnIoCheck <= 0)
        {
            _pawnIoCheck = 10; // registry lookup about every 3 s
            _installPawnIo.Visible = !PawnIoModule.Installed && (PerformanceManager.TdpSupported || PerformanceManager.FanSupported);
            _installRtss.Visible = _settings.CapFps && Rtss.InstallDir() == null;
        }
        var p = _perf;
        string temp = double.IsNaN(p.Temperature) ? "-" : $"{p.Temperature:0} °C";
        string fan = p.FanRpm < 0 ? "-" : $"{p.FanRpm} rpm" + (p.FanPercent >= 0 ? $" ({p.FanPercent}%)" : " (auto)");
        string power = p.AppliedWatts > 0 ? $"{p.AppliedWatts} W" : "default";
        _perfLive.Text = $"CPU {temp}     Fan {fan}     Power {power}";
        var lines = new[] { p.TdpStatus, p.FanStatus, p.FpsStatus, "CPU: " + PerformanceManager.CpuName }
            .Where(l => !string.IsNullOrEmpty(l));
        string status = string.Join("\n", lines);
        if (_perfStatus.Text != status) _perfStatus.Text = status;
    }

    static (GroupBox, TableLayoutPanel) Group(string title)
    {
        var group = new GroupBox { Text = title, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(4, 0, 4, 1) };
        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        // Narrow enough that two groups side by side fit a handheld's 1280-wide (1080p at 150%) screen.
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 212));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        group.Controls.Add(grid);
        return (group, grid);
    }

    static readonly AppSettings Defaults = new();

    /// <summary>Counter-clockwise circular arrow (fonts render ↺ too small and off-centre).</summary>
    static void DrawResetArrow(Graphics g, Rectangle bounds, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float r = Math.Min(bounds.Width, bounds.Height) * 0.26f;
        float cx = bounds.X + bounds.Width / 2f, cy = bounds.Y + bounds.Height / 2f;
        using var pen = new Pen(color, 1.8f);
        // Open at the top-left; the arrowhead sits at the arc's start, pointing back counter-clockwise.
        g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, -60, 290);
        double a = -60 * Math.PI / 180;
        var tip = new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a));
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, new[]
        {
            new PointF(tip.X - 3.5f, tip.Y - 3.5f),
            new PointF(tip.X + 3.5f, tip.Y - 1.0f),
            new PointF(tip.X - 1.0f, tip.Y + 3.5f),
        });
    }

    /// <summary>Slider row: label, bar, value and a reset-to-default button. <paramref name="value"/> reads the
    /// slider position from a settings object (the pending edits, or the defaults for the reset button).</summary>
    void AddSlider(TableLayoutPanel grid, int row, string label, int min, int max, Func<AppSettings, int> value,
        Func<int, string> format, Action<int> apply, string? tip = null, int step = 1,
        AppSettings? source = null, Action? onChange = null)
    {
        int defaultValue = Math.Clamp(value(Defaults), min, max);
        var lbl = new Label { Text = label, AutoSize = true, Margin = new Padding(3, 5, 3, 1) };
        var bar = new MaterialSlider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value(source ?? _pending), min, max),
            Width = 205,
            Height = 24,
            Margin = new Padding(2, 1, 2, 1),
            SmallChange = step,
            LargeChange = step * 2,
        };
        // Fixed width so the reset buttons line up whatever the value text is.
        var val = new Label { AutoSize = false, Size = new Size(56, 18), Margin = new Padding(2, 5, 0, 1), Text = format(bar.Value) };
        var reset = new Button
        {
            Size = new Size(24, 22),
            Margin = new Padding(0, 1, 2, 1),
            FlatStyle = FlatStyle.Flat,
            Enabled = bar.Value != defaultValue,
        };
        DarkTheme.SetIcon(reset, DrawResetArrow);
        reset.Click += (_, _) => bar.Value = defaultValue;
        bar.ValueChanged += (_, _) =>
        {
            // Dragging moves in 1s; snap to the step (re-enters once with the snapped value).
            int snapped = Math.Clamp((int)Math.Round((bar.Value - min) / (double)step) * step + min, min, max);
            if (snapped != bar.Value)
            {
                bar.Value = snapped;
                return;
            }
            val.Text = format(bar.Value);
            reset.Enabled = bar.Value != defaultValue;
            apply(bar.Value);
            (onChange ?? Changed)();
        };
        var tt = new ToolTip();
        tt.SetToolTip(reset, $"Reset to default ({format(defaultValue)})");
        if (tip != null)
        {
            tt.SetToolTip(lbl, tip);
            tt.SetToolTip(bar, tip);
        }
        var end = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        end.Controls.Add(val);
        end.Controls.Add(reset);
        grid.Controls.Add(lbl, 0, row);
        grid.Controls.Add(bar, 1, row);
        grid.Controls.Add(end, 2, row);
    }

    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        var open = menu.Items.Add("Open settings", null, (_, _) => ShowFromTray());
        open.Font = new Font(open.Font, FontStyle.Bold);
        _trayToggle.Click += (_, _) => _power.Checked = !_power.Checked;
        menu.Items.Add(_trayToggle);
        var perfMenu = new ToolStripMenuItem("Performance");
        foreach (var (text, value) in ProfileChoices)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (_, _) => SetProfile(value);
            _profileMenu.Add((item, value));
            perfMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(perfMenu);
        menu.Items.Add(_profilesMenu);
        StyleProfiles();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        DarkTheme.Apply(menu);
        _tray.ContextMenuStrip = menu;
        _tray.Icon = _settings.Enabled ? _trayOn : _trayOff;
        _tray.Text = "DS Gyro 360";
        _tray.Visible = true;
        // Left click toggles ON/OFF (the icon turns grey/colored); double click opens the window.
        // The toggle waits out the double-click time so a double click doesn't switch OFF and back ON.
        var clickTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime };
        clickTimer.Tick += (_, _) =>
        {
            clickTimer.Stop();
            _power.Checked = !_power.Checked;
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) clickTimer.Start();
        };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            clickTimer.Stop();
            ShowFromTray();
        };
    }

    // ---------- behaviour ----------

    bool _dirty;

    void Changed() => StyleApply(dirty: true);

    /// <summary>Makes the edited options live, saves them and re-applies hiding/autostart.</summary>
    void Apply()
    {
        bool autostartChanged = _pending.StartWithWindows != _settings.StartWithWindows;
        _settings.CopyOptionsFrom(_pending);
        if (autostartChanged && Autostart.Set(_settings.StartWithWindows) is { } error)
            MessageBox.Show(this, "Couldn't set up Start with Windows:\n\n" + error, "DS Gyro 360",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        SaveActiveProfile();
        _settings.Save();
        _manager.SettingsChanged();
        StyleApply(dirty: false);
    }

    void StyleApply(bool dirty)
    {
        _dirty = dirty;
        _apply.Enabled = dirty;
        _apply.Text = dirty ? "Apply" : "Applied ✓";
        _apply.BackColor = DarkTheme.Primary; // shown greyed out by the theme while there's nothing to apply
        _apply.ForeColor = DarkTheme.OnPrimary;
        _apply.Invalidate();
    }

    void SetEnabled(bool on)
    {
        _settings.Enabled = on;
        StylePower();
        _manager.SettingsChanged();
        _settings.Save();
    }

    void StylePower()
    {
        bool on = _power.Checked;
        _power.Text = on ? "ON" : "OFF";
        // Drawn by the theme from these: a Material filled button when on, muted when off.
        _power.BackColor = on ? DarkTheme.Primary : DarkTheme.Field;
        _power.ForeColor = on ? DarkTheme.OnPrimary : DarkTheme.SubtleText;
        _power.Invalidate();
        _trayToggle.Text = on ? "Turn off" : "Turn on";
        _tray.Icon = on ? _trayOn : _trayOff;
    }

    void RefreshStatus()
    {
        var pads = _manager.Snapshot();
        var builtIn = _manager.BuiltInStatus;
        _driverStatus.Text = $"ViGEmBus: OK   {_manager.HidHideStatus}\n{pads.Count} controller(s) found" +
            (builtIn.Length > 0 ? "\n" + builtIn : "");

        _list.BeginUpdate();
        while (_list.Items.Count > pads.Count) _list.Items.RemoveAt(_list.Items.Count - 1);
        for (int i = 0; i < pads.Count; i++)
        {
            var p = pads[i];
            string[] cells =
            {
                p.Slot >= 0 ? $"Player {p.Slot + 1}" : "-",
                p.DisplayName,
                p.Kind == PadKind.Handheld ? "Built-in" : p.Bluetooth ? "Bluetooth" : "USB",
                p.Battery < 0 ? "-" : $"{p.Battery}%" + (p.Charging ? " ⚡" : ""),
                !_settings.GyroEnabled ? "off" : p.GyroActive ? "● active" : "idle",
                p.Status,
            };
            if (i >= _list.Items.Count) _list.Items.Add(new ListViewItem(cells));
            var item = _list.Items[i];
            for (int c = 0; c < cells.Length; c++)
                if (item.SubItems[c].Text != cells[c]) item.SubItems[c].Text = cells[c];
        }
        _list.EndUpdate();
        if (_page == Page.Performance && Visible) RefreshPerformance();
        _tray.Text =$"DS Gyro 360 - {(_settings.Enabled ? "ON" : "OFF")}, {pads.Count} controller(s)";
    }

    void ShowFromTray()
    {
        _allowShow = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!_allowShow)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTheme.ApplyTitleBar(this);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && _settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        if (_dirty)
        {
            var answer = MessageBox.Show(this, "Apply your unapplied changes before exiting?", "DS Gyro 360",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
            {
                e.Cancel = true;
                _exiting = false;
                return;
            }
            if (answer == DialogResult.Yes) Apply();
        }
        _refresh.Stop();
        _autoSwitch.Stop();
        SaveActiveProfile();
        _settings.Save();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }
}

/// <summary>Draws the window / tray icon so no image files are needed.</summary>
static class AppIcon
{
    public static Icon Create()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico");
        if (stream != null) return new Icon(stream);

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(16, 124, 16));
            g.FillEllipse(brush, 1, 1, 30, 30);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Tray-sized icon: full color when ON, dimmed greyscale when OFF.</summary>
    public static Icon CreateTray(bool on)
    {
        var size = SystemInformation.SmallIconSize;
        using var source = new Icon(Create(), size);
        if (on) return (Icon)source.Clone();

        using var color = source.ToBitmap();
        using var grey = new Bitmap(color.Width, color.Height);
        using (var g = Graphics.FromImage(grey))
        {
            // Luminance greyscale, slightly faded so OFF reads as inactive next to other tray icons.
            var matrix = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                new[] { 0f, 0, 0, 0.85f, 0 },
                new[] { 0.05f, 0.05f, 0.05f, 0, 1 },
            });
            using var attrs = new System.Drawing.Imaging.ImageAttributes();
            attrs.SetColorMatrix(matrix);
            g.DrawImage(color, new Rectangle(0, 0, color.Width, color.Height),
                0, 0, color.Width, color.Height, GraphicsUnit.Pixel, attrs);
        }
        return Icon.FromHandle(grey.GetHicon());
    }
}
