using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle(Program.Name)]
[assembly: AssemblyProduct(Program.Name)]
[assembly: AssemblyDescription("Windows keyboard layout conversion and snippet expansion")]
[assembly: AssemblyVersion("0.1.2.0")]
[assembly: AssemblyFileVersion("0.1.2.0")]
[assembly: AssemblyInformationalVersion("0.1.2")]

internal static class Program
{
    internal const string Name = "KeyRewind";

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool firstInstance;
        // Earlier builds must also release their input hooks before another instance starts.
        using (var singleInstance = new Mutex(true, "Local\\LayoutAndSnippets", out firstInstance))
        {
            if (!firstInstance) return;
            try
            {
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("KeyRewind.ico"))
                using (var windowIcon = new Icon(stream, SystemInformation.IconSize))
                using (var trayIcon = new Icon(windowIcon, SystemInformation.SmallIconSize))
                using (var gamingTrayIcon = TrayIcons.WithGamingBadge(trayIcon))
                using (var app = new TrayApp(windowIcon, trayIcon, gamingTrayIcon))
                    Application.Run(app);
            }
            catch (Exception error) { MessageBox.Show(error.Message, Name, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private const string GamingModeTooltip = Program.Name + " — Gaming Mode on";
    private readonly Control dispatcher = new Control();
    private readonly KeyboardEngine engine;
    private readonly NotifyIcon icon;
    private readonly TrayMenu menu;
    private readonly ToolStripMenuItem gamingMode;
    private readonly Icon windowIcon;
    private readonly Icon trayIcon;
    private readonly Icon gamingTrayIcon;
    private Settings settings;
    private ModernSettingsForm form;
    private bool exiting;

    internal TrayApp(Icon windowIcon, Icon trayIcon, Icon gamingTrayIcon)
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
        menu.AddAction("Exit", TrayMenuIcon.Exit, (sender, args) => BeginExit());
        icon = new NotifyIcon { ContextMenuStrip = menu };
        SetGamingIndicator(settings.GamingMode, settings.GamingMode ? GamingModeTooltip : Program.Name);
        icon.Visible = true;
        icon.MouseClick += (sender, args) =>
        {
            if (args.Button == MouseButtons.Left) OpenSettings();
        };
        if (!settings.LaunchInBackground) dispatcher.BeginInvoke((Action)OpenSettings);
    }

    private void ShowNotice(string message)
    {
        if (!exiting) icon.ShowBalloonTip(1800, Program.Name, message, ToolTipIcon.Info);
    }

    private void OpenSettings()
    {
        if (exiting) return;
        if (form != null)
        {
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            return;
        }
        form = new ModernSettingsForm(settings, engine, windowIcon, updated =>
        {
            updated.GamingMode = settings.GamingMode;
            updated.CloseToTrayNoticeShown = settings.CloseToTrayNoticeShown;
            updated.Save();
            settings = updated;
            engine.Apply(updated);
        });
        form.FormClosed += (sender, args) =>
        {
            engine.Recorded = null;
            form = null;
            if (settings.ExitOnClose) BeginExit();
            else if (!exiting && args.CloseReason == CloseReason.UserClosing && !settings.CloseToTrayNoticeShown)
            {
                settings.CloseToTrayNoticeShown = true;
                ShowNotice("Still running in the background. Choose Exit from the tray menu to quit.");
                try { settings.Save(); }
                catch (Exception error)
                {
                    if (!(error is InvalidDataException || error is IOException ||
                        error is UnauthorizedAccessException || error is SerializationException)) throw;
                    Debug.WriteLine(error.Message);
                }
            }
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
            engine.Dispose();
            icon.Dispose();
            menu.Dispose();
            dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
