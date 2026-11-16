using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Gyro2stick;

public enum PerfProfile { Off, Fps30, Fps45, Fps60 }
public enum FanMode { Auto, Curve, Fixed }

// =====================================================================================================
// PawnIO: signed kernel driver that runs signed "modules" (RyzenSMU, LpcIO). Replaces WinRing0, which
// Windows Defender now blocks as a vulnerable driver. https://pawnio.eu
// =====================================================================================================

/// <summary>One PawnIO module loaded into its own driver handle.</summary>
sealed class PawnIoModule : IDisposable
{
    delegate int OpenFn(out IntPtr handle);
    delegate int LoadFn(IntPtr handle, byte[] blob, UIntPtr size);
    delegate int ExecuteFn(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr returned);
    delegate int CloseFn(IntPtr handle);

    static IntPtr _lib;
    static OpenFn? _open;
    static LoadFn? _load;
    static ExecuteFn? _execute;
    static CloseFn? _close;
    static readonly object LibLock = new();

    IntPtr _handle;
    readonly object _lock = new();

    PawnIoModule(IntPtr handle) => _handle = handle;

    /// <summary>Folder PawnIO was installed to, or null if it isn't installed.</summary>
    public static string? InstallDir()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var k = uninstall.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is string dn && dn.StartsWith("PawnIO", StringComparison.OrdinalIgnoreCase)
                        && k.GetValue("InstallLocation") is string loc && File.Exists(Path.Combine(loc, "PawnIOLib.dll")))
                        return loc;
                }
            }
            catch { }
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
        return File.Exists(Path.Combine(fallback, "PawnIOLib.dll")) ? fallback : null;
    }

    public static bool Installed => InstallDir() != null;

    static bool EnsureLib()
    {
        lock (LibLock)
        {
            if (_lib != IntPtr.Zero) return true;
            var dir = InstallDir();
            if (dir == null || !NativeLibrary.TryLoad(Path.Combine(dir, "PawnIOLib.dll"), out var lib)) return false;
            T Fn<T>(string n) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, n));
            _open = Fn<OpenFn>("pawnio_open");
            _load = Fn<LoadFn>("pawnio_load");
            _execute = Fn<ExecuteFn>("pawnio_execute");
            _close = Fn<CloseFn>("pawnio_close");
            _lib = lib;
            return true;
        }
    }

    /// <summary>Opens the driver and loads an embedded module (e.g. "RyzenSMU.bin"). Null with a reason on failure.</summary>
    public static PawnIoModule? Load(string resource, out string? error)
    {
        error = null;
        if (!EnsureLib())
        {
            error = "PawnIO driver not installed";
            return null;
        }
        using var s = typeof(PawnIoModule).Assembly.GetManifestResourceStream(resource);
        if (s == null)
        {
            error = resource + " missing from the app";
            return null;
        }
        var blob = new byte[s.Length];
        s.ReadExactly(blob);
        int hr = _open!(out var h);
        if (hr < 0)
        {
            error = $"PawnIO open failed (0x{hr:X8})";
            return null;
        }
        hr = _load!(h, blob, (UIntPtr)blob.Length);
        if (hr < 0)
        {
            _close!(h);
            error = $"{resource} rejected by PawnIO (0x{hr:X8}) - not supported on this PC";
            return null;
        }
        return new PawnIoModule(h);
    }

    /// <summary>Calls a module function. Throws on failure.</summary>
    public ulong[] Call(string name, int outCount, params ulong[] input)
    {
        var output = new ulong[Math.Max(outCount, 1)];
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(PawnIoModule));
            int hr = _execute!(_handle, name, input, (UIntPtr)input.Length, output, (UIntPtr)outCount, out _);
            if (hr < 0) throw new IOException($"{name} failed (0x{hr:X8})");
        }
        return output;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_handle != IntPtr.Zero) _close?.Invoke(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

static class PawnIoSetup
{
    /// <summary>Runs the bundled, signed PawnIO installer silently. Returns null on success, else the problem.</summary>
    public static string? Install()
    {
        var path = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
        try
        {
            using (var s = typeof(PawnIoSetup).Assembly.GetManifestResourceStream("PawnIO_setup.exe"))
            using (var f = File.Create(path))
                s!.CopyTo(f);
            using var p = Process.Start(new ProcessStartInfo(path, "-install -silent") { UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return "The installer did not start.";
            if (!p.WaitForExit(120000)) return "The installer timed out.";
            // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED
            if (p.ExitCode != 0 && p.ExitCode != 3010) return $"The installer failed (code {p.ExitCode}).";
            return PawnIoModule.Installed ? null : "Installed, but PawnIOLib.dll was not found. Restart Windows and try again.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

// =====================================================================================================
// AMD SMU (Ryzen 6000 "Rembrandt"): power limits through the MP1 mailbox, CPU temperature (Tctl).
// Message IDs as used by RyzenAdj for Renoir and newer: STAPM 0x14, fast PPT 0x15, slow PPT 0x16 (mW).
// =====================================================================================================

sealed class RyzenSmu : IDisposable
{
    const uint Mp1Msg = 0x3B10528, Mp1Rsp = 0x3B10578, Mp1Args = 0x3B10998;
    const uint ThmTconCurTmp = 0x59800;
    readonly PawnIoModule _m;
    readonly Mutex _pci = new(false, @"Global\Access_PCI"); // shared with other hardware tools

    RyzenSmu(PawnIoModule m) => _m = m;

    /// <summary>Ryzen 6000 mobile (Rembrandt / Rembrandt-R).</summary>
    public static bool IsSupportedCpu => System.Text.RegularExpressions.Regex.IsMatch(Cpu.Name,
        @"Ryzen\s+\d\s+(PRO\s+)?(6\d{2}0(U|H|HS|HX)|7\d35(U|H|HS))\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static RyzenSmu? Open(out string? error)
    {
        var m = PawnIoModule.Load("RyzenSMU.bin", out error);
        return m == null ? null : new RyzenSmu(m);
    }

    T Locked<T>(Func<T> f)
    {
        bool owned = false;
        try
        {
            try { owned = _pci.WaitOne(200); } catch (AbandonedMutexException) { owned = true; }
            return f();
        }
        finally { if (owned) _pci.ReleaseMutex(); }
    }

    uint Read(uint addr) => (uint)_m.Call("ioctl_read_smu_register", 1, addr)[0];
    void Write(uint addr, uint value) => _m.Call("ioctl_write_smu_register", 0, addr, value);

    /// <summary>Sends one MP1 message with one argument; true if the SMU answered OK.</summary>
    bool SendMp1(uint msg, uint arg) => Locked(() =>
    {
        var sw = Stopwatch.StartNew();
        while (Read(Mp1Rsp) == 0 && sw.ElapsedMilliseconds < 100) Thread.Sleep(1); // previous command still busy
        Write(Mp1Rsp, 0);
        Write(Mp1Args, arg);
        for (uint i = 1; i < 6; i++) Write(Mp1Args + i * 4, 0);
        Write(Mp1Msg, msg);
        sw.Restart();
        uint rsp;
        while ((rsp = Read(Mp1Rsp)) == 0 && sw.ElapsedMilliseconds < 500) Thread.Sleep(1);
        return rsp == 1;
    });

    /// <summary>Sets sustained (STAPM), slow and fast power limits to the same wattage.</summary>
    public bool SetPowerLimit(int watts)
    {
        uint mw = (uint)Math.Clamp(watts, 4, 54) * 1000;
        bool ok = SendMp1(0x14, mw);
        ok &= SendMp1(0x16, mw);
        ok &= SendMp1(0x15, mw);
        return ok;
    }

    /// <summary>CPU temperature (Tctl) in °C.</summary>
    public double ReadTemperature()
    {
        uint raw = Locked(() => Read(ThmTconCurTmp));
        double t = (raw >> 21) * 0.125;
        if ((raw & 0x80000) != 0) t -= 49; // extended range
        return t;
    }

    public void Dispose()
    {
        _m.Dispose();
        _pci.Dispose();
    }
}

static class Cpu
{
    public static readonly string Name = Read();

    static string Read()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
        }
        catch { return ""; }
    }
}

// =====================================================================================================
// GPD Win 4 (6800U) fan, through its embedded controller's RAM: index/data ports 0x2E/0x2F, registers
// 0x11/0x10 = address high/low, 0x12 = data. Fan PWM register 0xC311 (0 = EC automatic, 1..127 = manual),
// fan speed 0xC880/0xC881 (rpm, big-endian).
// =====================================================================================================

sealed class GpdWin4Fan : IDisposable
{
    const ushort PwmReg = 0xC311, RpmReg = 0xC880;
    const int PwmMax = 127;
    readonly PawnIoModule _m;
    readonly Mutex _isa = new(false, @"Global\Access_ISABUS.HTP.Method"); // shared with other hardware tools

    GpdWin4Fan(PawnIoModule m) => _m = m;

    public static bool IsGpdWin4_6800U =>
        Handheld.DeviceName == "GPD Win 4" && Cpu.Name.Contains("6800U", StringComparison.OrdinalIgnoreCase);

    public static GpdWin4Fan? Open(out string? error)
    {
        var m = PawnIoModule.Load("LpcIO.bin", out error);
        if (m == null) return null;
        try
        {
            m.Call("ioctl_select_slot", 0, 0); // 0x2E / 0x2F
            var fan = new GpdWin4Fan(m);
            fan.InitEc();
            return fan;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            m.Dispose();
            return null;
        }
    }

    T Locked<T>(Func<T> f)
    {
        bool owned = false;
        try
        {
            try { owned = _isa.WaitOne(200); } catch (AbandonedMutexException) { owned = true; }
            return f();
        }
        finally { if (owned) _isa.ReleaseMutex(); }
    }

    void Reg(byte reg, byte value) => _m.Call("ioctl_superio_outb", 0, reg, value);

    void SetAddress(ushort addr)
    {
        Reg(0x2E, 0x11);
        Reg(0x2F, (byte)(addr >> 8));
        Reg(0x2E, 0x10);
        Reg(0x2F, (byte)addr);
        Reg(0x2E, 0x12);
    }

    byte ReadRam(ushort addr) => Locked(() =>
    {
        SetAddress(addr);
        return (byte)_m.Call("ioctl_superio_inb", 1, 0x2F)[0];
    });

    void WriteRam(ushort addr, byte value) => Locked(() =>
    {
        SetAddress(addr);
        Reg(0x2F, value);
        return 0;
    });

    /// <summary>
    /// Some Win 4 6800U firmware leaves the EC uninitialised at boot (fan speed reads 0, fan writes are ignored)
    /// until bit 7 of 0x1060 is set. Only done on the expected EC chip (ID 0x55).
    /// </summary>
    void InitEc()
    {
        if (ReadRam(0x2000) != 0x55) return;
        byte v = ReadRam(0x1060);
        if ((v & 0x80) == 0) WriteRam(0x1060, (byte)(v | 0x80));
    }

    public int ReadRpm() => ReadRam(RpmReg) << 8 | ReadRam((ushort)(RpmReg + 1));

    /// <summary>Hands the fan back to the EC's own automatic control.</summary>
    public void SetAuto() => WriteRam(PwmReg, 0);

    /// <summary>Fixed fan speed, 0..100 %.</summary>
    public void SetPercent(int percent)
    {
        int pwm255 = Math.Clamp(percent, 0, 100) * 255 / 100;
        WriteRam(PwmReg, (byte)(Math.Round(pwm255 * (PwmMax - 1) / 255.0) + 1));
    }

    public void Dispose()
    {
        try { SetAuto(); } catch { }
        _m.Dispose();
        _isa.Dispose();
    }
}

// =====================================================================================================
// RivaTuner Statistics Server frame limiter (global profile), through RTSSHooks64.dll from the RTSS SDK.
// =====================================================================================================

static class Rtss
{
    delegate bool ProfileFn([MarshalAs(UnmanagedType.LPStr)] string profile);
    delegate bool SetPropertyFn([MarshalAs(UnmanagedType.LPStr)] string name, ref int data, uint size);
    delegate void UpdateFn();

    public static string? InstallDir()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var k = root.OpenSubKey(@"SOFTWARE\Unwinder\RTSS");
            if (k?.GetValue("InstallDir") is string d && File.Exists(Path.Combine(d, "RTSSHooks64.dll"))) return d;
        }
        catch { }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server");
        return File.Exists(Path.Combine(fallback, "RTSSHooks64.dll")) ? fallback : null;
    }

    static IntPtr _lib;

    /// <summary>Sets the global frame-rate limit (0 = unlimited) and starts RTSS if needed. Null on success.</summary>
    public static string? SetLimit(int fps)
    {
        var dir = InstallDir();
        if (dir == null) return "RivaTuner Statistics Server is not installed";
        try
        {
            if (_lib == IntPtr.Zero && !NativeLibrary.TryLoad(Path.Combine(dir, "RTSSHooks64.dll"), out _lib))
                return "RTSSHooks64.dll could not be loaded";
            T Fn<T>(string n) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, n));
            Fn<ProfileFn>("LoadProfile")("");
            int value = Math.Max(0, fps);
            if (!Fn<SetPropertyFn>("SetProfileProperty")("FramerateLimit", ref value, 4))
                return "RTSS did not accept the frame limit";
            if (!Fn<ProfileFn>("SaveProfile")(""))
                return "RTSS could not save its profile (needs admin rights)";
            Fn<UpdateFn>("UpdateProfiles")();

            if (fps > 0 && Process.GetProcessesByName("RTSS").Length == 0)
            {
                var exe = Path.Combine(dir, "RTSS.exe");
                if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>
/// RivaTuner can't be bundled, but for offline PCs its installer (RTSSSetup*.exe, or the zip from Guru3D that
/// contains it) can be copied next to Gyro2stick.exe and run from the app.
/// </summary>
static class RtssSetup
{
    static string AppDir => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>The installer (or zip) found next to the exe, newest first; null if none.</summary>
    public static string? FindLocal()
    {
        try
        {
            return Directory.EnumerateFiles(AppDir)
                .Where(f =>
                {
                    var n = Path.GetFileName(f);
                    bool rtss = n.StartsWith("RTSS", StringComparison.OrdinalIgnoreCase)
                                || n.Contains("RivaTuner", StringComparison.OrdinalIgnoreCase);
                    var ext = Path.GetExtension(f);
                    return rtss && (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                           && !n.Equals("RTSS.exe", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Runs the local installer with its own setup window and waits. Null on success, else the problem.</summary>
    public static string? Install(string path)
    {
        string? tempDir = null;
        try
        {
            string setup = path;
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                tempDir = Path.Combine(Path.GetTempPath(), "Gyro2stick_RTSS");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                System.IO.Compression.ZipFile.ExtractToDirectory(path, tempDir);
                setup = Directory.EnumerateFiles(tempDir, "*.exe", SearchOption.AllDirectories)
                            .FirstOrDefault(f => Path.GetFileName(f).Contains("Setup", StringComparison.OrdinalIgnoreCase))
                        ?? throw new FileNotFoundException("No setup .exe inside " + Path.GetFileName(path));
            }
            using var p = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(setup)! });
            if (p == null) return "The installer did not start.";
            p.WaitForExit();
            return Rtss.InstallDir() != null ? null : "RivaTuner was not installed (setup cancelled?).";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            if (tempDir != null) try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}

// =====================================================================================================
// Applies the selected performance profile: power limit, frame cap and fan curve, on a worker thread.
// =====================================================================================================

public sealed class PerformanceManager : IDisposable
{
    readonly AppSettings _settings;
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    volatile bool _running = true;

    RyzenSmu? _smu;
    GpdWin4Fan? _fan;
    long _nextOpenTry;

    // Live status for the UI.
    public double Temperature { get; private set; } = double.NaN;
    public int FanRpm { get; private set; } = -1;
    public int FanPercent { get; private set; } = -1; // -1 = EC automatic
    public int AppliedWatts { get; private set; }
    public string TdpStatus { get; private set; } = "";
    public string FanStatus { get; private set; } = "";
    public string FpsStatus { get; private set; } = "";

    public static bool TdpSupported => RyzenSmu.IsSupportedCpu;
    public static bool FanSupported => GpdWin4Fan.IsGpdWin4_6800U;
    public static string CpuName => Cpu.Name;

    /// <summary>Default fan curves (°C → %), quieter for lower frame rates.</summary>
    static readonly (double T, double P)[] Curve30 = { (0, 0), (50, 20), (60, 30), (70, 45), (80, 70), (88, 100) };
    static readonly (double T, double P)[] Curve45 = { (0, 0), (45, 25), (60, 40), (70, 60), (80, 85), (88, 100) };
    static readonly (double T, double P)[] Curve60 = { (0, 0), (40, 30), (55, 50), (65, 70), (75, 90), (85, 100) };

    public PerformanceManager(AppSettings settings)
    {
        _settings = settings;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Performance" };
        _thread.Start();
    }

    /// <summary>Re-apply now (profile, fan mode or wattage changed, or PawnIO was just installed).</summary>
    public void Changed()
    {
        _nextOpenTry = 0;
        _lastWatts = -1;
        _lastFps = -1;
        _wake.Set();
    }

    public static int WattsFor(AppSettings s, PerfProfile p) => p switch
    {
        PerfProfile.Fps30 => s.TdpFps30,
        PerfProfile.Fps45 => s.TdpFps45,
        PerfProfile.Fps60 => s.TdpFps60,
        _ => 0,
    };

    static int FpsFor(PerfProfile p) => p switch { PerfProfile.Fps30 => 30, PerfProfile.Fps45 => 45, PerfProfile.Fps60 => 60, _ => 0 };

    int _lastWatts = -1, _lastFps = -1;
    long _lastTdpApply;
    double _fanSmoothed = -1;
    int _fanWritten = -2; // -1 = auto written
    int _tempFailures;

    void Loop()
    {
        while (_running)
        {
            try { Tick(); } catch { /* keep going; status shows the problem */ }
            _wake.WaitOne(1000);
        }
    }

    void OpenHardware()
    {
        if (Environment.TickCount64 < _nextOpenTry) return;
        _nextOpenTry = Environment.TickCount64 + 15000;
        string? err = null;
        if (_smu == null && TdpSupported) _smu = RyzenSmu.Open(out err);
        if (_smu == null) TdpStatus = !TdpSupported ? $"Power limits: not supported on this CPU ({Cpu.Name})" : "Power limits: " + err;
        if (_fan == null && FanSupported) _fan = GpdWin4Fan.Open(out err);
        if (_fan == null) FanStatus = !FanSupported ? "Fan control: GPD Win 4 (6800U) only" : "Fan control: " + err;
    }

    void Tick()
    {
        var st = _settings;
        if (_smu == null || (_fan == null && FanSupported)) OpenHardware();
        var profile = st.PerfProfile;

        // Power limit: re-sent every 10 s, since firmware and other tools can reset it.
        int watts = WattsFor(st, profile);
        if (_smu != null)
        {
            if (watts > 0 && (watts != _lastWatts || Environment.TickCount64 - _lastTdpApply > 10000))
            {
                bool ok = _smu.SetPowerLimit(watts);
                _lastWatts = watts;
                _lastTdpApply = Environment.TickCount64;
                AppliedWatts = ok ? watts : 0;
                TdpStatus = ok ? $"Power limit: {watts} W" : "Power limit: the SMU did not accept the limit";
            }
            else if (watts == 0)
            {
                _lastWatts = 0;
                AppliedWatts = 0;
                TdpStatus = "Power limit: not changed (Off)";
            }
            try
            {
                Temperature = _smu.ReadTemperature();
                _tempFailures = 0;
            }
            catch
            {
                Temperature = double.NaN;
                _tempFailures++;
            }
        }

        // Frame cap
        int fps = st.CapFps ? FpsFor(profile) : 0;
        if (fps != _lastFps)
        {
            _lastFps = fps;
            if (fps == 0 && !st.RtssCapSet) FpsStatus = st.CapFps ? "" : "Frame cap: off";
            else
            {
                var err = Rtss.SetLimit(fps);
                if (err == null)
                {
                    st.RtssCapSet = fps > 0;
                    st.Save();
                }
                FpsStatus = err != null ? "Frame cap: " + err : fps > 0 ? $"Frame cap: {fps} fps (RTSS)" : "Frame cap: off";
            }
        }

        // Fan
        if (_fan != null)
        {
            try
            {
                FanRpm = _fan.ReadRpm();
                UpdateFan(st, profile);
            }
            catch (Exception ex)
            {
                FanStatus = "Fan control: " + ex.Message;
            }
        }
    }

    void UpdateFan(AppSettings st, PerfProfile profile)
    {
        var mode = st.FanMode;
        // No temperature, or a curve with no profile selected: leave it to the EC.
        bool noTemp = double.IsNaN(Temperature) || _tempFailures > 2;
        if (mode == FanMode.Curve && (profile == PerfProfile.Off || noTemp)) mode = FanMode.Auto;
        if (!noTemp && Temperature >= 95) mode = FanMode.Fixed; // safety: full speed when very hot

        if (mode == FanMode.Auto)
        {
            if (_fanWritten != -1) _fan!.SetAuto();
            _fanWritten = -1;
            _fanSmoothed = -1;
            FanPercent = -1;
            FanStatus = "Fan: automatic (GPD)";
            return;
        }

        double target = !noTemp && Temperature >= 95 ? 100
            : mode == FanMode.Fixed ? st.FanFixedPercent
            : Interp(profile switch { PerfProfile.Fps30 => Curve30, PerfProfile.Fps45 => Curve45, _ => Curve60 }, Temperature);
        // Ramp up quickly, slow down gently, so the fan doesn't pulse.
        if (_fanSmoothed < 0) _fanSmoothed = target;
        else _fanSmoothed += Math.Clamp(target - _fanSmoothed, -2, 8);
        int percent = (int)Math.Round(_fanSmoothed);
        if (percent != _fanWritten)
        {
            _fan!.SetPercent(percent);
            _fanWritten = percent;
        }
        FanPercent = percent;
        FanStatus = mode == FanMode.Fixed && Temperature < 95 ? $"Fan: fixed {percent}%" : $"Fan: curve {percent}%";
    }

    static double Interp((double T, double P)[] curve, double t)
    {
        if (t <= curve[0].T) return curve[0].P;
        for (int i = 1; i < curve.Length; i++)
            if (t <= curve[i].T)
            {
                var (t0, p0) = curve[i - 1];
                var (t1, p1) = curve[i];
                return p0 + (p1 - p0) * (t - t0) / (t1 - t0);
            }
        return curve[^1].P;
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread.Join(3000);
        try
        {
            // Don't leave our cap on games after the app has closed.
            if (_settings.RtssCapSet && Rtss.SetLimit(0) == null)
            {
                _settings.RtssCapSet = false;
                _settings.Save();
            }
        }
        catch { }
        _fan?.Dispose(); // back to automatic
        _smu?.Dispose();
    }
}
