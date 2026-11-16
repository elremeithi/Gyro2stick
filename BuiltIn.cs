using System.Runtime.InteropServices;
using Microsoft.Win32;
using Nefarius.Utilities.DeviceManagement.PnP;
using Windows.Devices.Sensors;

namespace Gyro2stick;

/// <summary>Whether the handheld's own controller is taken over (Auto = only on known handhelds such as the GPD Win 4).</summary>
public enum BuiltInMode { Auto, On, Off }

/// <summary>A signed axis of the Windows motion sensor (display frame: X right, Y up the screen, Z out of the screen).</summary>
public enum SensorAxis { PlusX, MinusX, PlusY, MinusY, PlusZ, MinusZ }

/// <summary>
/// Handheld PCs whose gamepad is a built-in XInput controller and whose gyro is exposed through the Windows sensor
/// API (GPD Win 4: Bosch BMI260 with GPD's driver).
/// </summary>
public static class Handheld
{
    internal static readonly Lazy<(string Maker, string Model)> Bios = new(() =>
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return ((key?.GetValue("SystemManufacturer") as string ?? "").Trim(),
                    (key?.GetValue("SystemProductName") as string ?? "").Trim());
        }
        catch { return ("", ""); }
    });

    static readonly string[] KnownMakers = { "GPD", "AYANEO", "AYA NEO", "ONE-NETBOOK", "ONEXPLAYER" };

    /// <summary>True on handhelds where Auto mode turns the built-in controller + gyro on.</summary>
    public static bool IsKnownHandheld => KnownMakers.Any(m => Bios.Value.Maker.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static string DeviceName
    {
        get
        {
            var (maker, model) = Bios.Value;
            if (maker.Contains("GPD", StringComparison.OrdinalIgnoreCase))
                return model.StartsWith("G1618-04", StringComparison.OrdinalIgnoreCase) ? "GPD Win 4" : "GPD built-in";
            return "Built-in";
        }
    }

    public sealed record Pad(string InterfacePath, string InstanceId, ushort Vid, ushort Pid);

    /// <summary>
    /// The built-in wired XInput controller: a physical (not ViGEm) USB device with the XUSB interface.
    /// Bluetooth Xbox pads are skipped. Prefers the Microsoft 360 IDs that handhelds report.
    /// </summary>
    public static Pad? FindBuiltInPad()
    {
        var found = new List<Pad>();
        for (int i = 0; i < 16; i++)
        {
            string path = "", instanceId = "";
            try
            {
                if (!Devcon.FindByInterfaceGuid(DeviceInterfaceIds.XUsbDevice, out path, out instanceId, i, true)) break;
            }
            catch { break; }
            if (!instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || IsVirtual(instanceId)) continue;
            var (vid, pid) = ParseIds(instanceId);
            found.Add(new Pad(path, instanceId, vid, pid));
        }
        return found.FirstOrDefault(p => p.Vid == 0x045E && p.Pid == 0x028E) ?? found.FirstOrDefault();
    }

    /// <summary>ViGEm pads (ours and other apps') hang off a root-enumerated bus device.</summary>
    static bool IsVirtual(string instanceId)
    {
        try
        {
            IPnPDevice? d = PnPDevice.GetDeviceByInstanceId(instanceId);
            for (int depth = 0; d != null && depth < 16; depth++, d = d.Parent)
                if (d.InstanceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { return true; }
        return false;
    }

    static (ushort vid, ushort pid) ParseIds(string instanceId)
    {
        static ushort Hex(string s, string tag)
        {
            int i = s.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            return i >= 0 && i + tag.Length + 4 <= s.Length &&
                   ushort.TryParse(s.AsSpan(i + tag.Length, 4), System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : (ushort)0;
        }
        return (Hex(instanceId, "VID_"), Hex(instanceId, "PID_"));
    }
}

/// <summary>
/// GPD's motion-sensor driver, bundled under Drivers\GpdSensor (embedded as "GpdSensor/..."), installed with
/// pnputil when a GPD has no gyro sensor (e.g. after a clean Windows install).
/// </summary>
public static class SensorDriverSetup
{
    const string Prefix = "GpdSensor/";

    static string[] Resources => typeof(SensorDriverSetup).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();

    public static bool Bundled => Resources.Any(n => n.EndsWith(".inf", StringComparison.OrdinalIgnoreCase));

    /// <summary>True on a GPD whose gyro sensor is missing while the driver is bundled.</summary>
    public static bool Needed()
    {
        if (!Bundled || !Handheld.Bios.Value.Maker.Contains("GPD", StringComparison.OrdinalIgnoreCase)) return false;
        using var gyro = BuiltInGyro.TryCreate();
        return gyro == null;
    }

    /// <summary>Extracts the driver and installs it. Null on success, else the problem.</summary>
    public static string? Install()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Gyro2stick_GpdSensor");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            foreach (var name in Resources)
            {
                var path = Path.Combine(dir, name[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var s = typeof(SensorDriverSetup).Assembly.GetManifestResourceStream(name)!;
                using var f = File.Create(path);
                s.CopyTo(f);
            }
            var psi = new System.Diagnostics.ProcessStartInfo("pnputil.exe", $"/add-driver \"{dir}\\*.inf\" /subdirs /install")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return "pnputil could not be started";
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            // 0 = done, 3010 = done, restart needed. 259 = nothing to install (already up to date).
            return p.ExitCode is 0 or 3010 or 259 ? null : $"pnputil exit code {p.ExitCode}:\n{output.Trim()}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

/// <summary>The Windows gyrometer, read on its own event thread; the latest turn rate in deg/s (sensor frame).</summary>
public sealed class BuiltInGyro : IDisposable
{
    readonly Gyrometer _sensor;
    double _x, _y, _z;
    long _lastTicks;
    readonly object _lock = new();

    BuiltInGyro(Gyrometer sensor)
    {
        _sensor = sensor;
        try { _sensor.ReportInterval = Math.Max(_sensor.MinimumReportInterval, 4); } catch { }
        _sensor.ReadingChanged += OnReading;
    }

    public static BuiltInGyro? TryCreate()
    {
        try
        {
            var s = Gyrometer.GetDefault();
            return s == null ? null : new BuiltInGyro(s);
        }
        catch { return null; }
    }

    void OnReading(Gyrometer sender, GyrometerReadingChangedEventArgs e)
    {
        var r = e.Reading;
        lock (_lock)
        {
            _x = r.AngularVelocityX;
            _y = r.AngularVelocityY;
            _z = r.AngularVelocityZ;
            _lastTicks = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Latest reading; <paramref name="ageMs"/> is how long ago it arrived (long.MaxValue if never). Some drivers
    /// stop reporting while the device is still, so an old reading counts as zero instead of a constant turn.
    /// </summary>
    public (double x, double y, double z) Read(out long ageMs)
    {
        lock (_lock)
        {
            ageMs = _lastTicks == 0 ? long.MaxValue : Environment.TickCount64 - _lastTicks;
            uint interval = 16;
            try { interval = Math.Max(_sensor.ReportInterval, 1); } catch { }
            return ageMs > Math.Max(4 * interval, 60) ? (0, 0, 0) : (_x, _y, _z);
        }
    }

    public static double Pick(SensorAxis axis, double x, double y, double z) => axis switch
    {
        SensorAxis.PlusX => x,
        SensorAxis.MinusX => -x,
        SensorAxis.PlusY => y,
        SensorAxis.MinusY => -y,
        SensorAxis.PlusZ => z,
        _ => -z,
    };

    /// <summary>The unsigned axis (0 = X, 1 = Y, 2 = Z).</summary>
    public static int Index(SensorAxis a) => (int)a / 2;

    public void Dispose()
    {
        try { _sensor.ReadingChanged -= OnReading; } catch { }
    }
}

/// <summary>XInput, including the undocumented Guide-button and vendor-ID calls (as used by SDL).</summary>
static class XInput
{
    public const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008,
        Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
        LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
        public uint Reserved; // the Ex call writes one more DWORD
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Vibration { public ushort Left, Right; }

    [StructLayout(LayoutKind.Sequential)]
    struct CapabilitiesEx
    {
        public byte Type, SubType;
        public ushort Flags;
        public ushort Buttons;
        public byte LT, RT;
        public short LX, LY, RX, RY;
        public ushort VibLeft, VibRight;
        public ushort VendorId, ProductId, ProductVersion, Unk1;
        public uint Unk2;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern int XInputGetStateEx(int index, out State state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int XInputGetState(int index, out State state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")] static extern int XInputSetState(int index, ref Vibration v);
    [DllImport("xinput1_4.dll", EntryPoint = "#108")] static extern int XInputGetCapabilitiesEx(int reserved, int index, int flags, out CapabilitiesEx caps);

    static bool _noEx;

    /// <summary>Reads a slot (with the Guide button where supported). False if nothing is connected there.</summary>
    public static bool TryGetState(int index, out State state)
    {
        if (!_noEx)
        {
            try { return XInputGetStateEx(index, out state) == 0; }
            catch (EntryPointNotFoundException) { _noEx = true; }
        }
        return XInputGetState(index, out state) == 0;
    }

    public static void SetRumble(int index, byte large, byte small)
    {
        var v = new Vibration { Left = (ushort)(large * 257), Right = (ushort)(small * 257) };
        try { XInputSetState(index, ref v); } catch { }
    }

    /// <summary>Vendor / product ID of the pad in a slot, or null if unknown or not connected.</summary>
    public static (ushort vid, ushort pid)? GetIds(int index)
    {
        try
        {
            return XInputGetCapabilitiesEx(1, index, 0, out var c) == 0 ? (c.VendorId, c.ProductId) : null;
        }
        catch { return null; }
    }
}
