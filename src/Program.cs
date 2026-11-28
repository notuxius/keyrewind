using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.AccessControl;
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
    internal const string ReleaseVersion = "0.5.0";
    // Earlier builds must also release their input hooks before another instance starts.
    private const string InstanceName = @"Local\LayoutAndSnippets";
    private const string AdministratorArgument = "--admin";
    internal static bool RunningAsAdministrator { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        bool administratorLaunch = args.Length == 1 && args[0] == AdministratorArgument;
        try
        {
            if (args.Length == 0 || administratorLaunch)
            {
                if (ActivateRunningInstance()) return;
                RunningAsAdministrator = StandardUserLaunch.IsAdministrator();
                if (administratorLaunch && !RunningAsAdministrator)
                {
                    LaunchAsAdministrator();
                    return;
                }
            }
            else if (StandardUserLaunch.RestartIfElevated(TrayNotification.RegisterAfterLaunch))
            {
                // An older updater can still be waiting on the process it launched.
                using (var handoff = UpdateHandoff.ReadRestart(args))
                    if (handoff != null && handoff.Installing) handoff.WaitForHelperExit();
                return;
            }
        }
        catch (Win32Exception error)
        {
            UiText.UseLanguage(Settings.ReadLanguage());
            string message = UiText.Get(error.Message);
            if (args.Length != 0)
                message = UiText.Get(administratorLaunch ? "KeyRewind could not start as administrator." :
                    "KeyRewind could not start with standard-user permissions.") + "\r\n\r\n" + message;
            MessageBox.Show(message, Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

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
                {
                    UiText.UseLanguage(Settings.ReadLanguage());
                    MessageBox.Show(UiText.Get(error.Message), Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    private static bool InstanceIsRunning()
    {
        Mutex instance;
        try
        {
            if (!Mutex.TryOpenExisting(InstanceName, MutexRights.Synchronize, out instance)) return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        instance.Dispose();
        return true;
    }

    private static bool ActivateRunningInstance()
    {
        if (!InstanceIsRunning()) return false;
        SettingsActivation.Request();
        return true;
    }

    private static void LaunchAsAdministrator()
    {
        bool firstRequest;
        using (new Mutex(false, @"Local\KeyRewind.AdministratorLaunch", out firstRequest))
        {
            if (!firstRequest || ActivateRunningInstance()) return;
            try
            {
                using (var child = Process.Start(new ProcessStartInfo(Application.ExecutablePath, AdministratorArgument)
                    { UseShellExecute = true, Verb = "runas" }))
                {
                    // Keep the launch request active through consent and until the child claims the instance mutex.
                    while (!InstanceIsRunning() && !child.WaitForExit(25)) { }
                }
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode != 1223) throw; // ERROR_CANCELLED
            }
        }
    }

    private static Mutex OpenInstanceMutex(UpdateHandoff.Restart restart, out bool firstInstance)
    {
        if (restart != null && restart.Installing) return new Mutex(true, InstanceName, out firstInstance);
        using (var gate = UpdateHandoff.EnterStartup())
        {
            try { return new Mutex(true, InstanceName, out firstInstance); }
            finally { gate.ReleaseMutex(); }
        }
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly Control dispatcher = new Control();
    private readonly SettingsActivation activation;
    private readonly KeyboardEngine engine;
    private readonly TrayNotification icon;
    private readonly TrayMenu menu;
    private readonly ToolStripMenuItem openSettings, gamingMode, closeAndUpdate, exit;
    private string trayTooltip = Program.Name;
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
        UiText.UseLanguage(settings.Language);
        engine = new KeyboardEngine(dispatcher, settings, ShowNotice);
        menu = new TrayMenu(() => settings.Appearance, () => icon.Text);
        openSettings = menu.AddAction("", TrayMenuIcon.Settings, (sender, args) => OpenSettings());
        gamingMode = menu.AddAction("", TrayMenuIcon.Gamepad, (sender, args) => ToggleGamingMode());
        menu.Items.Add(new ToolStripSeparator());
        closeAndUpdate = menu.AddAction("", TrayMenuIcon.Update, (sender, args) => CloseAndUpdate());
        closeAndUpdate.Available = false;
        exit = menu.AddAction("", TrayMenuIcon.Exit, (sender, args) => BeginExit());
        icon = new TrayNotification(trayIcon, menu, OpenSettings);
        SetGamingIndicator(settings.GamingMode, settings.GamingMode ? TrayNotification.GamingModeTooltip : Program.Name);
        RefreshLanguage();
        icon.Show();
        if (!settings.LaunchInBackground) dispatcher.BeginInvoke((Action)OpenSettings);
        activation = new SettingsActivation(OpenSettings);
        updateTimer.Tick += (sender, args) => CheckForUpdates();
        dispatcher.BeginInvoke((Action)(() =>
        {
            if (exiting) return;
            if (restart != null)
            {
                restart.Ready();
                ShowNotice(restart.Installing ? UiText.Get("Updated to v{0}.", Program.ReleaseVersion) :
                    UiText.Get("The update failed. KeyRewind reopened with the previous version."));
            }
            CheckForUpdates();
        }));
    }

    private void ShowNotice(string message)
    {
        if (!exiting) icon.ShowNotice(message);
    }

    private void RefreshLanguage()
    {
        UiText.UseLanguage(settings.Language);
        menu.RightToLeft = UiText.Direction;
        openSettings.Text = UiText.Get("Settings");
        gamingMode.Text = UiText.Get("Gaming Mode");
        closeAndUpdate.Text = UiText.Get(closeAndUpdate.Enabled ? "Close and update" : "Downloading update…");
        exit.Text = UiText.Get("Exit");
        SetGamingIndicator(gamingMode.Checked, trayTooltip);
    }

    private async void CheckForUpdates()
    {
        updateTimer.Stop();
        if (exiting || Program.RunningAsAdministrator) return;
        if (DateTimeOffset.UtcNow >= nextUpdateCheck)
        {
            nextUpdateCheck = DateTimeOffset.UtcNow.AddDays(1);
            var cancellation = updateCancellation.Token;
            try
            {
                var release = await Task.Run(() => ReleaseUpdate.Check(new Version(Program.ReleaseVersion), cancellation));
                if (exiting) return;
                availableUpdate = release;
                closeAndUpdate.Available = release != null;
                if (release != null) NotifyUpdate(release.Version);
            }
            catch (UpdateRateLimitException error)
            {
                nextUpdateCheck = error.RetryAt;
            }
            catch (Exception error)
            {
                if (!IsUpdateError(error)) throw;
                Debug.WriteLine(error.Message);
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
        ShowNotice(UiText.Get("KeyRewind v{0} is available. Choose Close and update in the tray menu.", version));
    }

    private async void CloseAndUpdate()
    {
        var release = availableUpdate;
        if (exiting || release == null || !closeAndUpdate.Enabled) return;
        closeAndUpdate.Enabled = false;
        closeAndUpdate.Text = UiText.Get("Downloading update…");
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
            if (!exiting) ShowNotice(UiText.Get("Could not install the update. {0}", UiText.Get(error.Message)));
        }
        finally
        {
            if (!exiting)
            {
                closeAndUpdate.Text = UiText.Get("Close and update");
                closeAndUpdate.Enabled = true;
            }
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
            if (!exiting && UiText.Language != settings.Language) RefreshLanguage();
            bool showNotice = !settings.ExitOnClose && !exiting && args.CloseReason == CloseReason.UserClosing &&
                !settings.CloseToTrayNoticeShown;
            if (showNotice)
            {
                settings.CloseToTrayNoticeShown = true;
                ShowNotice(UiText.Get("Still running in the background. Choose Exit from the tray menu to quit."));
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
                if (gamingMode.Checked) ShowNotice(UiText.Get("Could not restore shortcuts. {0}", UiText.Get(error.Message)));
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
        SetGamingIndicator(false, "KeyRewind — pausing shortcuts");
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
        SetGamingIndicator(hooksRemoved, !hooksRemoved ? "KeyRewind — shortcuts paused" :
            settings.GamingMode ? TrayNotification.GamingModeTooltip : "KeyRewind — Gaming Mode on (session only)");
        if (!hooksRemoved) ShowNotice(UiText.Get("Windows could not remove every input hook. Choose Exit before playing."));
    }

    private void SetGamingIndicator(bool active, string tooltip)
    {
        gamingMode.Checked = active;
        trayTooltip = tooltip;
        string display = UiText.Get(tooltip);
        if (Program.RunningAsAdministrator) display += " (" + UiText.Get("Administrator") + ")";
        icon.SetAppearance(active, active ? gamingTrayIcon : trayIcon, display);
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
            ShowNotice(UiText.Get("Could not save Gaming Mode. {0}", UiText.Get(error.Message)));
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
