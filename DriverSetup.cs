using System.Diagnostics;
using System.Reflection;
using System.Security;
using Nefarius.Drivers.HidHide;
using Nefarius.ViGEm.Client;

namespace DSGyro360;

/// <summary>Installs the bundled ViGEmBus / HidHide drivers when they are missing.</summary>
static class DriverSetup
{
    public static bool IsViGEmInstalled()
    {
        try
        {
            using var c = new ViGEmClient();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsHidHideInstalled()
    {
        try { return new HidHideControlService().IsInstalled; }
        catch { return false; }
    }

    /// <summary>Returns false if the app cannot run (ViGEmBus missing).</summary>
    public static bool EnsureDrivers()
    {
        bool vigem = IsViGEmInstalled();
        bool hidHide = IsHidHideInstalled();
        // PawnIO is only offered where the Performance page can use it (Ryzen 6000 mobile / GPD Win 4).
        bool pawnIo = !(PerformanceManager.TdpSupported || PerformanceManager.FanSupported) || PawnIoModule.Installed;
        // RivaTuner can't be bundled; offered only when its installer was copied next to the exe.
        string? rtssSetup = Rtss.InstallDir() == null ? RtssSetup.FindLocal() : null;
        bool sensor = !SensorDriverSetup.Needed();
        if (vigem && hidHide && pawnIo && sensor && rtssSetup == null) return true;

        var missing = new List<string>();
        if (!vigem) missing.Add("  • ViGEmBus (required) - creates the virtual Xbox 360 controllers");
        if (!hidHide) missing.Add("  • HidHide (recommended) - hides the real controller from games to avoid double input");
        if (!pawnIo) missing.Add("  • PawnIO (for the Performance page) - power limits and fan control");
        if (!sensor) missing.Add("  • GPD motion sensor driver - the built-in gyro");
        if (rtssSetup != null) missing.Add($"  • RivaTuner Statistics Server (frame cap) - from {Path.GetFileName(rtssSetup)} next to the app");

        var answer = MessageBox.Show(
            "The following drivers are not installed:\n\n" + string.Join("\n", missing) +
            "\n\nNo internet is needed. Install them now?\n" +
            "(ViGEmBus, HidHide and RivaTuner open their own setup windows; PawnIO and the sensor driver install silently.)",
            "DS Gyro 360 - Driver setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        bool installedHidHide = false;
        if (answer == DialogResult.Yes)
        {
            if (!vigem) RunEmbeddedInstaller("ViGEmBus_Setup.exe");
            if (!hidHide)
            {
                RunEmbeddedInstaller("HidHide_Setup.exe");
                installedHidHide = IsHidHideInstalled();
            }
            if (!pawnIo && PawnIoSetup.Install() is { } error)
                MessageBox.Show("PawnIO could not be installed:\n" + error +
                                "\n\nYou can retry from the Performance page.", "DS Gyro 360",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (!sensor && SensorDriverSetup.Install() is { } sensorError)
                MessageBox.Show("The GPD motion sensor driver could not be installed:\n" + sensorError, "DS Gyro 360",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (rtssSetup != null && RtssSetup.Install(rtssSetup) is { } rtssError)
                MessageBox.Show("RivaTuner could not be installed:\n" + rtssError +
                                "\n\nYou can retry from the Performance page.", "DS Gyro 360",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        if (!IsViGEmInstalled())
        {
            MessageBox.Show(
                "ViGEmBus is required to create virtual Xbox 360 controllers.\n" +
                "If you just installed it, please restart Windows and run the app again.",
                "DS Gyro 360", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        if (installedHidHide)
        {
            MessageBox.Show(
                "HidHide was installed. A Windows restart is needed before controllers can be hidden from games.\n" +
                "The app will still work now, but games may see both controllers until you restart.",
                "DS Gyro 360", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        return true;
    }

    static void RunEmbeddedInstaller(string resourceName)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "DSGyro360");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, resourceName);
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                             ?? throw new FileNotFoundException("Missing embedded installer " + resourceName))
            using (var fs = File.Create(file))
                res.CopyTo(fs);

            using var p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            p?.WaitForExit();
            try { File.Delete(file); } catch { }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not run " + resourceName + ":\n" + ex.Message, "DS Gyro 360",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

/// <summary>
/// Start-with-Windows via a logon scheduled task. The app needs admin rights (HidHide control),
/// and a "Run" registry entry cannot start an elevated app without a UAC prompt.
/// </summary>
static class Autostart
{
    const string TaskName = "DSGyro360";

    /// <summary>Creates (or removes) the logon task. Returns null on success, else what went wrong.</summary>
    public static string? Set(bool enable)
    {
        if (!enable)
        {
            RunSchtasks($"/Delete /F /TN {TaskName}");
            return null;
        }

        var exe = Environment.ProcessPath!;
        // The account's SID works for local, Microsoft and work/school (Entra ID) accounts alike, on any PC.
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                   ?? Environment.UserDomainName + "\\" + Environment.UserName;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--tray</Arguments></Exec>
              </Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "DSGyro360_task.xml");
        try
        {
            File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            var (code, output) = RunSchtasks($"/Create /F /TN {TaskName} /XML \"{tmp}\"");
            if (code != 0) return output.Length > 0 ? output : $"schtasks exit code {code}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }

        // Check it really points at this exe.
        var (qcode, task) = RunSchtasks($"/Query /TN {TaskName} /XML");
        return qcode == 0 && task.Contains(SecurityElement.Escape(exe), StringComparison.OrdinalIgnoreCase)
            ? null
            : "The task was not saved correctly.";
    }

    static (int code, string output) RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return (-1, "schtasks.exe could not be started");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000)) return (-1, "schtasks.exe timed out");
            return (p.ExitCode, (stderr.Result + stdout.Result).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
