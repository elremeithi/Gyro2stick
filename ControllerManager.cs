using HidSharp;
using Nefarius.Drivers.HidHide;
using Nefarius.Utilities.DeviceManagement.PnP;
using Nefarius.ViGEm.Client;

namespace Gyro2stick;

/// <summary>
/// Finds DualShock 4 / DualSense pads, runs a <see cref="DsController"/> per pad and keeps
/// HidHide's block list in sync so games only see the virtual Xbox 360 pads.
/// </summary>
public sealed class ControllerManager : IDisposable
{
    readonly AppSettings _settings;
    readonly ViGEmClient _client;
    readonly List<DsController> _pads = new();
    // Pads that failed to open, with the time of the failure. A freshly plugged-in USB pad often fails for a
    // moment while Windows is still starting it, so failed pads are retried after a short wait.
    readonly Dictionary<string, long> _failedPaths = new(StringComparer.OrdinalIgnoreCase);
    const int RetryFailedMs = 3000;
    readonly object _lock = new();
    readonly System.Threading.Timer _timer;
    readonly HidHideControlService? _hidHide;
    bool _disposed;

    public string HidHideStatus { get; private set; } = "";

    public ControllerManager(AppSettings settings, ViGEmClient client)
    {
        _settings = settings;
        _client = client;

        try
        {
            var hh = new HidHideControlService();
            if (hh.IsInstalled) _hidHide = hh;
            HidHideStatus = _hidHide != null ? "HidHide: OK" : "HidHide: not installed";
        }
        catch (Exception ex)
        {
            HidHideStatus = "HidHide: unavailable (" + ex.Message + ")";
        }

        // Unhide anything a previous run left hidden (e.g. after a crash).
        UnhideAll();

        DeviceList.Local.Changed += (_, _) => _timer?.Change(300, 2000);
        _timer = new System.Threading.Timer(_ => Scan(), null, 0, 2000);
    }

    public IReadOnlyList<DsController> Snapshot()
    {
        lock (_lock) return _pads.ToList();
    }

    public void RecalibrateAll()
    {
        foreach (var p in Snapshot()) p.RequestCalibration();
    }

    public void CalibrateRumbleAll()
    {
        foreach (var p in Snapshot()) p.RequestRumbleCalibration();
    }

    /// <summary>Re-applies hiding after the master switch or the hide option changes.</summary>
    public void SettingsChanged() => ThreadPool.QueueUserWorkItem(_ => Scan());

    void Scan()
    {
        lock (_lock)
        {
            if (_disposed) return;

            foreach (var dead in _pads.Where(p => p.IsDead).ToList())
            {
                _pads.Remove(dead);
                dead.Dispose();
                _failedPaths.Remove(dead.DevicePath);
            }

            IEnumerable<HidDevice> devices;
            try { devices = DeviceList.Local.GetHidDevices().Where(d => DsController.IsKnownVendor(d.VendorID)).ToList(); }
            catch { return; }

            foreach (var dev in devices)
            {
                string name;
                PadKind kind;
                try { if (!DsController.TryIdentify(dev, out kind, out name)) continue; }
                catch { continue; } // descriptor unreadable (device going away)
                if (_pads.Any(p => string.Equals(p.DevicePath, dev.DevicePath, StringComparison.OrdinalIgnoreCase))) continue;
                if (_failedPaths.TryGetValue(dev.DevicePath, out var failedAt) &&
                    Environment.TickCount64 - failedAt < RetryFailedMs) continue;

                try
                {
                    var pad = new DsController(dev, kind, name, _client, _settings);
                    try { pad.InstanceId = PnPDevice.GetInstanceIdFromInterfaceId(dev.DevicePath); } catch { }
                    _pads.Add(pad);
                    _failedPaths.Remove(dev.DevicePath);
                    pad.Start();
                }
                catch
                {
                    // Still starting up, opened exclusively by another app, or a stale Bluetooth node.
                    _failedPaths[dev.DevicePath] = Environment.TickCount64;
                }
            }

            ScanBuiltIn();
            SyncHiding();
        }
    }

    // ---------- built-in handheld controller + gyro ----------

    BuiltInGyro? _gyro;
    long _gyroCheckedAt = -1;
    string? _builtInFailedId;
    long _builtInFailedAt;

    /// <summary>Status line for the built-in controller, empty when the feature is off.</summary>
    public string BuiltInStatus { get; private set; } = "";

    /// <summary>The Windows gyro sensor, if the feature is on and one exists (also used by the axis wizard).</summary>
    public BuiltInGyro? Gyro
    {
        get { lock (_lock) return _gyro; }
    }

    bool BuiltInWanted => _settings.BuiltInController == BuiltInMode.On
        || (_settings.BuiltInController == BuiltInMode.Auto && Handheld.IsKnownHandheld);

    /// <summary>XInput slots of all virtual pads, or null while one of them hasn't been given a slot yet.</summary>
    ISet<int>? VirtualSlots()
    {
        // Called from the built-in pad's thread; never wait here, the scan may be disposing that pad.
        if (!Monitor.TryEnter(_lock)) return null;
        try
        {
            var slots = new HashSet<int>();
            foreach (var p in _pads)
            {
                if (!p.HasVirtual) continue;
                if (p.Slot < 0) return null;
                slots.Add(p.Slot);
            }
            return slots;
        }
        finally { Monitor.Exit(_lock); }
    }

    void ScanBuiltIn()
    {
        var existing = _pads.FirstOrDefault(p => p.Kind == PadKind.Handheld);
        if (!BuiltInWanted)
        {
            BuiltInStatus = "";
            if (existing != null)
            {
                _pads.Remove(existing);
                existing.Dispose();
                if (existing.InstanceId != null) Unhide(existing.InstanceId);
            }
            return;
        }
        if (existing != null) return;

        if (_gyro == null)
        {
            // Look for the sensor at most every 10 s (it can appear after its driver is installed).
            if (_gyroCheckedAt >= 0 && Environment.TickCount64 - _gyroCheckedAt < 10000) return;
            _gyroCheckedAt = Environment.TickCount64;
            _gyro = BuiltInGyro.TryCreate();
            if (_gyro == null)
            {
                BuiltInStatus = "Built-in gyro: no motion sensor found (install the device's gyro/BMI260 driver)";
                return;
            }
        }

        var pad = Handheld.FindBuiltInPad();
        if (pad == null)
        {
            BuiltInStatus = "Built-in gyro: OK, built-in controller not found (in mouse mode?)";
            return;
        }
        if (pad.InstanceId == _builtInFailedId && Environment.TickCount64 - _builtInFailedAt < RetryFailedMs) return;
        try
        {
            var c = new DsController(pad, _gyro, VirtualSlots, _client, _settings);
            _pads.Add(c);
            c.Start();
            BuiltInStatus = "Built-in gyro: OK";
            _builtInFailedId = null;
        }
        catch (Exception ex)
        {
            _builtInFailedId = pad.InstanceId;
            _builtInFailedAt = Environment.TickCount64;
            BuiltInStatus = "Built-in controller error: " + ex.Message;
        }
    }

    /// <summary>Gives one controller back to games (the built-in one when the feature is switched off).</summary>
    void Unhide(string instanceId)
    {
        if (_hidHide == null || !_settings.HiddenInstanceIds.Contains(instanceId, StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            _hidHide.RemoveBlockedInstanceId(instanceId);
            _settings.HiddenInstanceIds.RemoveAll(id => string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
        }
        catch (Exception ex)
        {
            HidHideStatus = "HidHide error: " + ex.Message;
        }
    }

    void SyncHiding()
    {
        if (_hidHide == null) return;
        bool hide = _settings.Enabled && _settings.HideControllers;
        try
        {
            if (!hide)
            {
                UnhideAll();
                return;
            }

            var exe = Environment.ProcessPath;
            if (exe != null && !_hidHide.ApplicationPaths.Any(p => string.Equals(p, exe, StringComparison.OrdinalIgnoreCase)))
                _hidHide.AddApplicationPath(exe, true);

            var blocked = _hidHide.BlockedInstanceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (var pad in _pads.Where(p => !p.IsDead && p.InstanceId != null))
            {
                var id = pad.InstanceId!;
                if (!blocked.Contains(id)) _hidHide.AddBlockedInstanceId(id);
                if (!_settings.HiddenInstanceIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.HiddenInstanceIds.Add(id);
                    changed = true;
                }
                if (!pad.Bluetooth) changed |= DisableUsbAudio(id);
            }
            if (_settings.HiddenInstanceIds.Count > 0 && !_hidHide.IsActive) _hidHide.IsActive = true;
            if (changed) _settings.Save();
        }
        catch (Exception ex)
        {
            HidHideStatus = "HidHide error: " + ex.Message;
        }
    }

    /// <summary>
    /// Over USB a pad also shows up as a sound card. Games with native PlayStation support find the pad through
    /// it even when HidHide hides the controller, switch to PlayStation mode and ignore the Xbox pad, so the
    /// audio function is disabled while the pad is hidden. Returns true if the settings changed.
    /// </summary>
    bool DisableUsbAudio(string hidInstanceId)
    {
        bool changed = false;
        try
        {
            // HID collection -> USB interface (MI_03) -> USB composite device, whose other interfaces are audio.
            var iface = PnPDevice.GetDeviceByInstanceId(hidInstanceId).Parent;
            var composite = iface?.Parent;
            if (composite == null || !composite.InstanceId.StartsWith(@"USB\VID_054C&", StringComparison.OrdinalIgnoreCase))
                return false;
            foreach (var child in composite.Children ?? Enumerable.Empty<IPnPDevice>())
            {
                if (string.Equals(child.InstanceId, iface!.InstanceId, StringComparison.OrdinalIgnoreCase)) continue;
                if (_settings.DisabledAudioIds.Contains(child.InstanceId, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    PnPDevice.GetDeviceByInstanceId(child.InstanceId).Disable();
                    _settings.DisabledAudioIds.Add(child.InstanceId);
                    changed = true;
                }
                catch (Exception ex)
                {
                    HidHideStatus = "Could not disable controller audio: " + ex.Message;
                }
            }
        }
        catch { /* pad unplugged meanwhile */ }
        return changed;
    }

    void EnableUsbAudio()
    {
        if (_settings.DisabledAudioIds.Count == 0) return;
        foreach (var id in _settings.DisabledAudioIds.ToList())
        {
            try
            {
                // The pad may be unplugged; the disabled flag sticks to it, so re-enable it anyway.
                PnPDevice.GetDeviceByInstanceId(id, DeviceLocationFlags.Phantom).Enable();
                _settings.DisabledAudioIds.Remove(id);
            }
            catch { /* kept for the next attempt */ }
        }
        _settings.Save();
    }

    void UnhideAll()
    {
        EnableUsbAudio();
        if (_hidHide == null || _settings.HiddenInstanceIds.Count == 0) return;
        try
        {
            foreach (var id in _settings.HiddenInstanceIds) _hidHide.RemoveBlockedInstanceId(id);
            _settings.HiddenInstanceIds.Clear();
            _settings.Save();
            // Only switch cloaking off if nothing else (from the user's own HidHide setup) is still blocked.
            if (!_hidHide.BlockedInstanceIds.Any()) _hidHide.IsActive = false;
        }
        catch (Exception ex)
        {
            HidHideStatus = "HidHide error: " + ex.Message;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            foreach (var p in _pads) p.Dispose();
            _pads.Clear();
            _gyro?.Dispose();
            UnhideAll();
        }
    }
}
