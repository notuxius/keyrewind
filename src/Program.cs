using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

[assembly: AssemblyTitle(Program.Name)]
[assembly: AssemblyProduct(Program.Name)]
[assembly: AssemblyDescription("Windows keyboard layout conversion and snippet expansion")]
[assembly: AssemblyVersion(Program.ReleaseVersion + ".0")]
[assembly: AssemblyFileVersion(Program.ReleaseVersion + ".0")]
[assembly: AssemblyInformationalVersion(Program.ReleaseVersion)]

internal static class Program
{
    internal const string Name = "KeyRewind";
    internal const string ReleaseVersion = "0.4.1";

    [STAThread]
    private static void Main(string[] args)
    {
        if (UpdateHandoff.TryRunInstaller(args)) return;
        using (var restart = UpdateHandoff.ReadRestart(args))
        {
            try
            {
                bool firstInstance;
                using (var singleInstance = OpenInstanceMutex(restart, out firstInstance))
                {
                    if (!firstInstance)
                    {
                        if (restart == null || !restart.Installing) SettingsActivation.Request();
                        return;
                    }

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var stream = typeof(Program).Assembly.GetManifestResourceStream("KeyRewind.ico"))
                    using (var windowIcon = new Icon(stream, SystemInformation.IconSize))
                    using (var trayIcon = new Icon(windowIcon, SystemInformation.SmallIconSize))
                    using (var gamingTrayIcon = TrayIcons.WithGamingBadge(trayIcon))
                    using (var app = new TrayApp(windowIcon, trayIcon, gamingTrayIcon, restart))
                        Application.Run(app);
                }
            }
            catch (Exception error)
            {
                if (restart == null || !restart.Installing)
                    MessageBox.Show(error.Message, Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private static Mutex OpenInstanceMutex(UpdateHandoff.Restart restart, out bool firstInstance)
    {
        // Earlier builds must also release their input hooks before another instance starts.
        const string instanceName = @"Local\LayoutAndSnippets";
        if (restart != null && restart.Installing) return new Mutex(true, instanceName, out firstInstance);
        using (var gate = UpdateHandoff.EnterStartup())
        {
            try { return new Mutex(true, instanceName, out firstInstance); }
            finally { gate.ReleaseMutex(); }
        }
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private const string GamingModeTooltip = Program.Name + " — Gaming Mode on";
    private readonly Control dispatcher = new Control();
    private readonly SettingsActivation activation;
    private readonly KeyboardEngine engine;
    private readonly NotifyIcon icon;
    private readonly TrayMenu menu;
    private readonly ToolStripMenuItem gamingMode;
    private readonly ToolStripMenuItem closeAndUpdate, viewReleases;
    private readonly CancellationTokenSource updateCancellation = new CancellationTokenSource();
    private readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
    private ReleaseUpdate availableUpdate;
    private DateTimeOffset nextUpdateCheck;
    private readonly Icon windowIcon;
    private readonly Icon trayIcon;
    private readonly Icon gamingTrayIcon;
    private Settings settings;
    private ModernSettingsForm form;
    private bool exiting;

    internal TrayApp(Icon windowIcon, Icon trayIcon, Icon gamingTrayIcon, UpdateHandoff.Restart restart = null)
    {
        this.windowIcon = windowIcon;
        this.trayIcon = trayIcon;
        this.gamingTrayIcon = gamingTrayIcon;
        settings = Settings.Load();
        engine = new KeyboardEngine(dispatcher, settings, ShowNotice);
        menu = new TrayMenu(() => settings.Appearance, () => icon.Text);
        menu.AddAction("Settings", TrayMenuIcon.Settings, (sender, args) => OpenSettings());
        gamingMode = menu.AddAction("Gaming Mode", TrayMenuIcon.Gamepad, (sender, args) => ToggleGamingMode());
        menu.Items.Add(new ToolStripSeparator());
        closeAndUpdate = menu.AddAction("Close and update", TrayMenuIcon.Update, (sender, args) => CloseAndUpdate());
        closeAndUpdate.Available = false;
        viewReleases = menu.AddAction("View releases", TrayMenuIcon.Update, (sender, args) => OpenReleases());
        viewReleases.Available = false;
        menu.AddAction("Exit", TrayMenuIcon.Exit, (sender, args) => BeginExit());
        icon = new NotifyIcon { ContextMenuStrip = menu };
        SetGamingIndicator(settings.GamingMode, settings.GamingMode ? GamingModeTooltip : Program.Name);
        icon.Visible = true;
        icon.MouseClick += (sender, args) =>
        {
            if (args.Button == MouseButtons.Left) OpenSettings();
        };
        if (!settings.LaunchInBackground) dispatcher.BeginInvoke((Action)OpenSettings);
        activation = new SettingsActivation(OpenSettings);
        updateTimer.Tick += (sender, args) => CheckForUpdates();
        dispatcher.BeginInvoke((Action)(() =>
        {
            if (exiting) return;
            if (restart != null)
            {
                restart.Ready();
                ShowNotice(restart.Installing ? "Updated to v" + Program.ReleaseVersion + "." :
                    "The update failed. KeyRewind reopened with the previous version.");
            }
            CheckForUpdates();
        }));
    }

    private void ShowNotice(string message)
    {
        if (!exiting) icon.ShowBalloonTip(1800, Program.Name, message, ToolTipIcon.Info);
    }

    private async void CheckForUpdates()
    {
        updateTimer.Stop();
        if (exiting) return;
        if (DateTimeOffset.UtcNow >= nextUpdateCheck)
        {
            nextUpdateCheck = DateTimeOffset.UtcNow.AddDays(1);
            var cancellation = updateCancellation.Token;
            try
            {
                var release = await Task.Run(() => ReleaseUpdate.Check(new Version(Program.ReleaseVersion), cancellation));
                if (exiting) return;
                availableUpdate = release;
                viewReleases.Available = false;
                closeAndUpdate.Available = release != null;
                if (release != null) NotifyUpdate(release.Version);
            }
            catch (UpdateRateLimitException error)
            {
                nextUpdateCheck = error.RetryAt;
                if (!exiting) viewReleases.Available = true;
            }
            catch (Exception error)
            {
                if (!IsUpdateError(error)) throw;
                Debug.WriteLine(error.Message);
                if (!exiting) viewReleases.Available = true;
            }
        }
        if (exiting) return;
        updateTimer.Interval = (int)Math.Min(int.MaxValue,
            Math.Max(1000, (nextUpdateCheck - DateTimeOffset.UtcNow).TotalMilliseconds));
        updateTimer.Start();
    }

    private void NotifyUpdate(Version version)
    {
        Version notified;
        if (Version.TryParse(settings.UpdateNotifiedVersion, out notified) && notified >= version) return;
        settings.UpdateNotifiedVersion = version.ToString();
        try { settings.Save(); }
        catch (Exception error)
        {
            if (!IsUpdateError(error)) throw;
            Debug.WriteLine(error.Message);
        }
        ShowNotice("KeyRewind v" + version + " is available. Choose Close and update in the tray menu.");
    }

    private async void CloseAndUpdate()
    {
        var release = availableUpdate;
        if (exiting || release == null || !closeAndUpdate.Enabled) return;
        closeAndUpdate.Enabled = false;
        closeAndUpdate.Text = "Downloading update…";
        var cancellation = updateCancellation.Token;
        try
        {
            var payload = await Task.Run(() => release.Download(cancellation));
            if (exiting || form != null && !form.ConfirmUpdate()) return;
            if (exiting) return; // The confirmation dialog runs its own message loop.
            UpdateHandoff.Prepare(Application.ExecutablePath, payload, release.Digest);
            BeginExit();
        }
        catch (Exception error)
        {
            if (!IsUpdateError(error)) throw;
            Debug.WriteLine(error.Message);
            if (!exiting)
            {
                viewReleases.Available = true;
                ShowNotice("Could not install the update. Choose View releases for a manual download. " + error.Message);
            }
        }
        finally
        {
            if (!exiting)
            {
                closeAndUpdate.Text = "Close and update";
                closeAndUpdate.Enabled = true;
            }
        }
    }

    private void OpenReleases()
    {
        try
        {
            using (Process.Start(new ProcessStartInfo(ReleaseUpdate.ReleasesPage) { UseShellExecute = true })) { }
        }
        catch (Exception error)
        {
            if (!IsUpdateError(error)) throw;
            ShowNotice("Could not open the browser. " + ReleaseUpdate.ReleasesPage);
        }
    }

    private static bool IsUpdateError(Exception error)
    {
        return UpdateHandoff.IsFailure(error) || error is WebException ||
            error is SerializationException || error is XmlException;
    }

    private void OpenSettings()
    {
        if (exiting) return;
        if (form != null)
        {
            form.BringForward();
            return;
        }
        form = new ModernSettingsForm(settings, engine, windowIcon, updated =>
        {
            updated.GamingMode = settings.GamingMode;
            updated.CloseToTrayNoticeShown = settings.CloseToTrayNoticeShown;
            updated.UpdateNotifiedVersion = settings.UpdateNotifiedVersion;
            updated.Save();
            settings = updated;
            engine.Apply(updated);
        });
        form.FormClosed += (sender, args) =>
        {
            engine.Recorded = null;
            bool placementChanged = form.RememberPlacement(settings);
            form = null;
            bool showNotice = !settings.ExitOnClose && !exiting && args.CloseReason == CloseReason.UserClosing &&
                !settings.CloseToTrayNoticeShown;
            if (showNotice)
            {
                settings.CloseToTrayNoticeShown = true;
                ShowNotice("Still running in the background. Choose Exit from the tray menu to quit.");
            }
            if (placementChanged || showNotice)
            {
                try { settings.Save(); }
                catch (Exception error)
                {
                    if (!(error is InvalidDataException || error is IOException ||
                        error is UnauthorizedAccessException || error is SerializationException)) throw;
                    Debug.WriteLine(error.Message);
                }
            }
            if (settings.ExitOnClose) BeginExit();
        };
        form.Show();
    }

    private async void ToggleGamingMode()
    {
        if (exiting || !gamingMode.Enabled) return;
        if (gamingMode.Checked)
        {
            try { engine.Enable(); }
            catch (Win32Exception error)
            {
                ShowPausedState(engine.Disable());
                if (gamingMode.Checked) ShowNotice("Could not restore shortcuts. " + error.Message);
                return;
            }
            if (!SaveGamingMode(false))
            {
                ShowPausedState(engine.Disable());
                return;
            }
            SetGamingIndicator(false, Program.Name);
            return;
        }

        gamingMode.Enabled = false;
        icon.Text = Program.Name + " — pausing shortcuts";
        if (form != null) form.StopRecording();
        bool hooksRemoved = engine.Disable();
        SaveGamingMode(true);
        // Let cancelled clipboard work clean up before reporting that the mode is active.
        while (!exiting && engine.Busy) await Task.Delay(20);
        if (exiting) return;

        ShowPausedState(hooksRemoved);
        gamingMode.Enabled = true;
    }

    private void ShowPausedState(bool hooksRemoved)
    {
        SetGamingIndicator(hooksRemoved, !hooksRemoved ? Program.Name + " — shortcuts paused" :
            settings.GamingMode ? GamingModeTooltip : GamingModeTooltip + " (session only)");
        if (!hooksRemoved) ShowNotice("Windows could not remove every input hook. Choose Exit before playing.");
    }

    private void SetGamingIndicator(bool active, string tooltip)
    {
        gamingMode.Checked = active;
        icon.Icon = active ? gamingTrayIcon : trayIcon;
        icon.Text = tooltip;
    }

    private bool SaveGamingMode(bool on)
    {
        if (settings.GamingMode == on) return true;
        settings.GamingMode = on;
        try { settings.Save(); }
        catch (Exception error)
        {
            if (!(error is InvalidDataException || error is IOException ||
                error is UnauthorizedAccessException || error is SerializationException)) throw;
            settings.GamingMode = !on;
            ShowNotice("Could not save Gaming Mode. " + error.Message);
            return false;
        }
        return true;
    }

    private async void BeginExit()
    {
        if (exiting) return;
        exiting = true;
        updateTimer.Stop();
        updateCancellation.Cancel();
        engine.Dispose();
        if (form != null) form.Close();
        var gracePeriod = Stopwatch.StartNew();
        while (engine.Busy && gracePeriod.ElapsedMilliseconds < 1200)
            await Task.Delay(20);
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            updateTimer.Dispose();
            updateCancellation.Dispose();
            activation.Dispose();
            engine.Dispose();
            icon.Dispose();
            menu.Dispose();
            dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
