using Nefarius.ViGEm.Client;

namespace DSGyro360;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "DSGyro360_SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("DS Gyro 360 is already running (check the system tray).", "DS Gyro 360",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        if (!DriverSetup.EnsureDrivers()) return;

        var settings = AppSettings.Load();
        // Keep the logon task pointing at this copy of the exe (it goes stale when the app is moved or republished).
        if (settings.StartWithWindows) ThreadPool.QueueUserWorkItem(_ => Autostart.Set(true));
        using var client = new ViGEmClient();
        using var manager = new ControllerManager(settings, client);
        using var performance = new PerformanceManager(settings);
        bool startHidden = args.Contains("--tray", StringComparer.OrdinalIgnoreCase) || settings.StartMinimized;
        Application.Run(new MainForm(settings, manager, performance, startHidden));
    }
}
