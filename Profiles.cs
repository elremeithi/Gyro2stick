using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DSGyro360;

/// <summary>A named set of options, optionally tied to games (exe names) that switch to it automatically.</summary>
public sealed class GameProfile
{
    public const string DefaultName = "Default";

    public string Name { get; set; } = DefaultName;
    /// <summary>Executable file names, e.g. "eldenring.exe".</summary>
    public List<string> Games { get; set; } = new();
    public AppSettings? Options { get; set; }

    public bool IsDefault => string.Equals(Name, DefaultName, StringComparison.OrdinalIgnoreCase);

    public bool Matches(string exe) => Games.Any(g => string.Equals(g, exe, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Profile bookkeeping, the exported file format, and finding the game in front.</summary>
static class ProfileStore
{
    sealed class ExportFile
    {
        public string App { get; set; } = "DS Gyro 360";
        public int Version { get; set; } = 1;
        public DateTime Exported { get; set; }
        public string ActiveProfile { get; set; } = GameProfile.DefaultName;
        public bool AutoSwitchProfiles { get; set; } = true;
        public List<GameProfile> Profiles { get; set; } = new();
    }

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Makes sure "Default" exists (first, created from the current options) and the active name is valid.</summary>
    public static void EnsureDefault(AppSettings s)
    {
        var def = s.Profiles.FirstOrDefault(p => p.IsDefault);
        if (def == null)
        {
            def = new GameProfile { Options = s.SnapshotProfile() };
            s.Profiles.Insert(0, def);
        }
        else if (s.Profiles[0] != def)
        {
            s.Profiles.Remove(def);
            s.Profiles.Insert(0, def);
        }
        foreach (var p in s.Profiles) p.Options ??= s.SnapshotProfile();
        if (Find(s, s.ActiveProfile) == null) s.ActiveProfile = def.Name;
    }

    public static GameProfile? Find(AppSettings s, string name) =>
        s.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string UniqueName(AppSettings s, string wanted)
    {
        wanted = string.IsNullOrWhiteSpace(wanted) ? "Profile" : wanted.Trim();
        if (Find(s, wanted) == null) return wanted;
        for (int i = 2; ; i++)
            if (Find(s, $"{wanted} ({i})") == null) return $"{wanted} ({i})";
    }

    public static void Export(AppSettings s, string path)
    {
        var file = new ExportFile
        {
            Exported = DateTime.Now,
            ActiveProfile = s.ActiveProfile,
            AutoSwitchProfiles = s.AutoSwitchProfiles,
            Profiles = s.Profiles,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpts));
    }

    /// <summary>Reads an exported file. Throws with a readable message if it isn't one.</summary>
    public static (List<GameProfile> Profiles, string Active, bool AutoSwitch) Import(string path)
    {
        ExportFile? file;
        try { file = JsonSerializer.Deserialize<ExportFile>(File.ReadAllText(path), JsonOpts); }
        catch (Exception ex) { throw new InvalidDataException("This isn't a DS Gyro 360 profile file (" + ex.Message + ")."); }
        if (file?.Profiles == null || file.Profiles.Count == 0)
            throw new InvalidDataException("The file has no profiles in it.");
        foreach (var p in file.Profiles)
        {
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? "Imported" : p.Name.Trim();
            p.Games = p.Games?.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
            p.Options ??= new AppSettings();
            p.Options.Profiles = new(); // never nested
        }
        return (file.Profiles, file.ActiveProfile, file.AutoSwitchProfiles);
    }

    /// <summary>Exe name of the window in front ("game.exe"), or null.</summary>
    public static string? ForegroundExe()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch { return null; }
    }

    /// <summary>Programs with a visible window right now (for "add running game"), as (exe, window title).</summary>
    public static List<(string Exe, string Title)> RunningApps()
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe", "ApplicationFrameHost.exe", "SystemSettings.exe", "TextInputHost.exe", "ShellExperienceHost.exe",
            "StartMenuExperienceHost.exe", "SearchHost.exe", "LockApp.exe", "dwm.exe",
        };
        var list = new List<(string, string)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    var exe = p.ProcessName + ".exe";
                    if (!skip.Contains(exe)) list.Add((exe, p.MainWindowTitle));
                }
                catch { }
            }
        }
        return list.DistinctBy(a => a.Item1, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}

/// <summary>Create, rename, delete and tie profiles to games; export and import (backup / restore).</summary>
sealed class ProfilesDialog : Form
{
    readonly AppSettings _s;
    readonly Action _saveActive;
    readonly ListBox _profiles = new();
    readonly ListBox _games = new();
    readonly TextBox _name = new();
    readonly Button _rename = new(), _addRunning = new(), _browse = new(), _removeGame = new(),
        _new = new(), _duplicate = new(), _delete = new(), _use = new(), _export = new(), _import = new(), _close = new();
    readonly CheckBox _auto = new();

    /// <summary>Profile to switch to when the dialog closes (set by "Use this profile"), or null.</summary>
    public string? UseProfile { get; private set; }
    /// <summary>True if the active profile's stored options were replaced (import), so they must be reloaded.</summary>
    public bool ActiveReplaced { get; private set; }

    public ProfilesDialog(AppSettings settings, Action saveActive)
    {
        _s = settings;
        _saveActive = saveActive;
        Text = "Profiles";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None; // positions below are in 96-dpi pixels, scaled at the end
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(640, 396);

        Label L(string text, int x, int y, int w = 300) => new() { Text = text, Left = x, Top = y, Width = w, AutoSize = false, Height = 20 };
        void B(Button b, string text, int x, int y, int w, EventHandler click)
        {
            b.Text = text;
            b.SetBounds(x, y, w, 30);
            b.Click += click;
        }

        Controls.Add(L("Profiles", 14, 12, 200));
        _profiles.SetBounds(14, 34, 200, 250);
        _profiles.IntegralHeight = false;
        _profiles.SelectedIndexChanged += (_, _) => ShowSelected();
        B(_new, "New", 14, 292, 64, (_, _) => NewProfile(copyOf: null));
        B(_duplicate, "Duplicate", 82, 292, 80, (_, _) => NewProfile(copyOf: Selected));
        B(_delete, "Delete", 166, 292, 48, (_, _) => DeleteSelected());
        _delete.Text = "✕";
        new ToolTip().SetToolTip(_delete, "Delete this profile");
        new ToolTip().SetToolTip(_new, "New profile with the options you're using now");

        Controls.Add(L("Name", 232, 12));
        _name.SetBounds(232, 34, 290, 26);
        _name.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { RenameSelected(); e.SuppressKeyPress = true; } };
        B(_rename, "Rename", 528, 32, 96, (_, _) => RenameSelected());

        Controls.Add(L("Games that switch to this profile while they're in front:", 232, 70, 392));
        _games.SetBounds(232, 92, 290, 150);
        _games.IntegralHeight = false;
        B(_addRunning, "Add running…", 528, 92, 96, (_, _) => AddRunning());
        B(_browse, "Browse .exe…", 528, 126, 96, (_, _) => BrowseExe());
        B(_removeGame, "Remove", 528, 160, 96, (_, _) => RemoveGame());

        _auto.Text = "Switch profiles automatically when a listed game is in front (back to your chosen profile after)";
        _auto.SetBounds(232, 250, 400, 40);
        _auto.Checked = _s.AutoSwitchProfiles;
        _auto.CheckedChanged += (_, _) => { _s.AutoSwitchProfiles = _auto.Checked; _s.Save(); };

        B(_use, "Use this profile", 232, 292, 140, (_, _) =>
        {
            if (Selected == null) return;
            UseProfile = Selected.Name;
            Close();
        });

        var line = new Label { BorderStyle = BorderStyle.None, Left = 14, Top = 336, Width = 610, Height = 1 };
        Controls.Add(line);
        B(_export, "Export all…", 14, 350, 120, (_, _) => Export());
        B(_import, "Import / restore…", 140, 350, 150, (_, _) => Import());
        new ToolTip().SetToolTip(_export, "Save every profile (with its games and options) to a .json file, as a backup or to copy to another PC.");
        new ToolTip().SetToolTip(_import, "Load profiles from an exported .json file: replace all of yours (restore a backup) or add them.");
        B(_close, "Close", 524, 350, 100, (_, _) => Close());
        CancelButton = _close;

        Controls.AddRange(new Control[] { _profiles, _new, _duplicate, _delete, _name, _rename, _games, _addRunning, _browse,
            _removeGame, _auto, _use, _export, _import, _close });
        DarkTheme.Apply(this);
        BackColor = DarkTheme.Background;
        ForeColor = DarkTheme.Text;
        line.BackColor = DarkTheme.Border;
        float k = DeviceDpi / 96f; // the font already follows the DPI; scale the fixed layout to match
        if (k > 1.01f) Scale(new SizeF(k, k));
        RefreshList(_s.ActiveProfile);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTheme.ApplyTitleBar(this);
    }

    GameProfile? Selected => _profiles.SelectedIndex >= 0 && _profiles.SelectedIndex < _s.Profiles.Count
        ? _s.Profiles[_profiles.SelectedIndex] : null;

    void RefreshList(string? select)
    {
        _profiles.BeginUpdate();
        _profiles.Items.Clear();
        foreach (var p in _s.Profiles)
        {
            string label = p.Name;
            if (string.Equals(p.Name, _s.ActiveProfile, StringComparison.OrdinalIgnoreCase)) label += "   (in use)";
            if (p.Games.Count > 0) label += $"   · {p.Games.Count} game" + (p.Games.Count == 1 ? "" : "s");
            _profiles.Items.Add(label);
        }
        _profiles.EndUpdate();
        int i = select == null ? 0 : _s.Profiles.FindIndex(p => string.Equals(p.Name, select, StringComparison.OrdinalIgnoreCase));
        _profiles.SelectedIndex = Math.Clamp(i, 0, _s.Profiles.Count - 1);
        ShowSelected();
    }

    void ShowSelected()
    {
        var p = Selected;
        _name.Text = p?.Name ?? "";
        bool editable = p != null && !p.IsDefault;
        _name.Enabled = _rename.Enabled = _delete.Enabled = editable;
        _addRunning.Enabled = _browse.Enabled = editable;
        _games.Items.Clear();
        if (p != null)
        {
            if (p.IsDefault) _games.Items.Add("(Default is used when no game profile applies)");
            else foreach (var g in p.Games) _games.Items.Add(g);
        }
        _removeGame.Enabled = editable && p!.Games.Count > 0;
    }

    void NewProfile(GameProfile? copyOf)
    {
        _saveActive(); // the copy gets the options in use right now
        var source = copyOf ?? ProfileStore.Find(_s, _s.ActiveProfile);
        var options = new AppSettings();
        options.CopyProfileFrom(source?.Options ?? _s.SnapshotProfile());
        var name = ProfileStore.UniqueName(_s, copyOf != null ? copyOf.Name + " copy" : "New profile");
        _s.Profiles.Add(new GameProfile { Name = name, Options = options });
        _s.Save();
        RefreshList(name);
        _name.Focus();
        _name.SelectAll();
    }

    void RenameSelected()
    {
        var p = Selected;
        if (p == null || p.IsDefault) return;
        var wanted = _name.Text.Trim();
        if (wanted.Length == 0 || string.Equals(wanted, p.Name, StringComparison.Ordinal)) return;
        if (ProfileStore.Find(_s, wanted) is { } other && other != p)
        {
            MessageBox.Show(this, $"There's already a profile called \"{wanted}\".", "Profiles", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        bool active = string.Equals(p.Name, _s.ActiveProfile, StringComparison.OrdinalIgnoreCase);
        p.Name = wanted;
        if (active) _s.ActiveProfile = wanted;
        _s.Save();
        RefreshList(wanted);
    }

    void DeleteSelected()
    {
        var p = Selected;
        if (p == null || p.IsDefault) return;
        if (MessageBox.Show(this, $"Delete the profile \"{p.Name}\"?", "Profiles", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        _s.Profiles.Remove(p);
        if (string.Equals(p.Name, _s.ActiveProfile, StringComparison.OrdinalIgnoreCase)) UseProfile = GameProfile.DefaultName;
        _s.Save();
        RefreshList(GameProfile.DefaultName);
    }

    void AddGame(string exe)
    {
        var p = Selected;
        if (p == null || p.IsDefault || string.IsNullOrWhiteSpace(exe)) return;
        exe = Path.GetFileName(exe.Trim());
        if (_s.Profiles.FirstOrDefault(o => o != p && o.Matches(exe)) is { } owner)
        {
            var move = MessageBox.Show(this, $"{exe} is already in the profile \"{owner.Name}\". Move it to \"{p.Name}\"?",
                "Profiles", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (move != DialogResult.OK) return;
            owner.Games.RemoveAll(g => string.Equals(g, exe, StringComparison.OrdinalIgnoreCase));
        }
        if (!p.Matches(exe)) p.Games.Add(exe);
        _s.Save();
        RefreshList(p.Name);
    }

    void AddRunning()
    {
        var menu = new ContextMenuStrip();
        var apps = ProfileStore.RunningApps();
        if (apps.Count == 0) menu.Items.Add("(no windows found)").Enabled = false;
        foreach (var (exe, title) in apps)
        {
            var t = title.Length > 50 ? title[..50] + "…" : title;
            menu.Items.Add($"{exe}   —   {t}", null, (_, _) => AddGame(exe));
        }
        DarkTheme.Apply(menu);
        menu.Show(_addRunning, new Point(0, _addRunning.Height));
    }

    void BrowseExe()
    {
        using var dlg = new OpenFileDialog { Title = "Choose the game's .exe", Filter = "Programs (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddGame(dlg.FileName);
    }

    void RemoveGame()
    {
        var p = Selected;
        if (p == null || p.IsDefault || _games.SelectedIndex < 0 || _games.SelectedIndex >= p.Games.Count) return;
        p.Games.RemoveAt(_games.SelectedIndex);
        _s.Save();
        RefreshList(p.Name);
    }

    void Export()
    {
        _saveActive();
        using var dlg = new SaveFileDialog
        {
            Title = "Export profiles",
            Filter = "DS Gyro 360 profiles (*.json)|*.json",
            FileName = $"DSGyro360-profiles-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ProfileStore.Export(_s, dlg.FileName);
            MessageBox.Show(this, $"Exported {_s.Profiles.Count} profile(s) to\n{dlg.FileName}", "Export profiles",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Export failed:\n" + ex.Message, "Export profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void Import()
    {
        using var dlg = new OpenFileDialog { Title = "Import profiles", Filter = "DS Gyro 360 profiles (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        List<GameProfile> incoming;
        string active;
        bool auto;
        try { (incoming, active, auto) = ProfileStore.Import(dlg.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(this,
            $"The file has {incoming.Count} profile(s): {string.Join(", ", incoming.Select(p => p.Name).Take(8))}{(incoming.Count > 8 ? ", …" : "")}\n\n" +
            "Yes: restore - replace ALL your profiles with these.\n" +
            "No: add them - profiles with the same name are overwritten, the rest are kept.",
            "Import profiles", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return;

        if (answer == DialogResult.Yes)
        {
            _s.Profiles = incoming;
            _s.AutoSwitchProfiles = auto;
            _auto.Checked = auto;
            ProfileStore.EnsureDefault(_s);
            UseProfile = ProfileStore.Find(_s, active)?.Name ?? GameProfile.DefaultName;
            ActiveReplaced = true;
        }
        else
        {
            foreach (var p in incoming)
            {
                var existing = ProfileStore.Find(_s, p.Name);
                if (existing != null)
                {
                    existing.Games = p.Games;
                    existing.Options = p.Options;
                    if (string.Equals(existing.Name, _s.ActiveProfile, StringComparison.OrdinalIgnoreCase)) ActiveReplaced = true;
                }
                else _s.Profiles.Add(p);
            }
            // A game can only belong to one profile: the imported one wins.
            foreach (var p in incoming)
                foreach (var g in p.Games)
                    foreach (var o in _s.Profiles.Where(o => !string.Equals(o.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
                        o.Games.RemoveAll(x => string.Equals(x, g, StringComparison.OrdinalIgnoreCase));
            ProfileStore.EnsureDefault(_s);
        }
        _s.Save();
        RefreshList(UseProfile ?? _s.ActiveProfile);
    }
}
