# KeyRewind

KeyRewind is a Windows tray utility for correcting text typed in the wrong keyboard layout and expanding short abbreviations.

## Use it

1. Download KeyRewind from [GitHub Releases](https://github.com/notuxius/keyrewind/releases). If you download `KeyRewind.zip`, extract both files. If Mahou is running, exit it from its tray icon. Open `KeyRewind.exe`. KeyRewind opens Settings on first launch.
2. Select editable text and press **Shift + Alt + Z**. KeyRewind converts the physical keystrokes between your two keyboard layouts and switches to the resulting layout. For example, `ghbdsn` becomes `привіт` with English and Ukrainian.
3. In **Settings**, record either shortcut, or use **Double Space** for snippets. The **Snippets** page has ten examples on a fresh install, including `krthanks`. Edit them or add your own abbreviations and single-line replacements. **Save changes** applies your changes and closes Settings. To reopen Settings while KeyRewind is running, left-click the tray icon or launch `KeyRewind.exe` again.
4. Type an abbreviation by itself and tap **Space** twice within about two-thirds of a second. KeyRewind replaces the abbreviation and consumes both spaces. With a custom snippet shortcut, press the shortcut immediately after typing the abbreviation.

When Windows has exactly two keyboard layouts, KeyRewind uses both. With more than two, choose the pair in **Settings → Shortcuts → Autoswitch layouts**. The conversion shortcut runs when one of those layouts is active. Snippet abbreviations are case-sensitive. Settings are saved locally in `%LOCALAPPDATA%\KeyRewind\settings.json`.

When conversion switches your keyboard layout, KeyRewind shows the new layout in a Windows notification. To turn these off, clear **Show autoswitch popups** under **Settings → Shortcuts → Autoswitch layouts** and select **Save changes**.

In **Settings → Appearance**, choose **System**, **Light**, or **Dark**, then select **Save changes**. System follows the Windows app color setting.

To change the interface language, open **Settings → General → Language**, choose a language, and select **Save changes**. The tray menu updates immediately. Reopen Settings to see it in the selected language.

KeyRewind remembers the Settings window’s position, size, and maximized state when you close it. If a monitor is disconnected, Settings reopens within the available screen space.

In **Settings → General**, check **Start with Windows** to run KeyRewind when you sign in. Enable **Launch in background** to start with only the tray icon visible. Select **Save changes** to apply your choices. If you move `KeyRewind.exe`, open it from its new location and save again to update the startup path.

Pin `KeyRewind.exe` to the taskbar or use it in a Start menu shortcut. The same executable starts KeyRewind at sign-in. A normal launch keeps the permissions Windows provides. Opening it from a standard desktop runs it with standard-user permissions. Opening it from an elevated File Explorer or terminal keeps the inherited administrator permissions without a new UAC prompt.

To request administrator permissions from a standard desktop, extract `KeyRewind.zip` and double-click **KeyRewind (Administrator)**. Keep the shortcut beside `KeyRewind.exe`. If KeyRewind is already running, either launch opens Settings. To switch permission levels, choose **Exit** from the tray menu first.

The tray tooltip shows **Administrator** for an elevated session.

**Close to tray** is enabled by default. The first time you close Settings to the tray, a notification tells you KeyRewind is still running in the background. Turn off **Close to tray** and select **Save changes** to exit KeyRewind when Settings closes.

## Update KeyRewind

In a standard-user session, right-click the tray icon and choose **Close and update** when a newer stable release is available. KeyRewind downloads and verifies the update first, then closes and reopens from the same location. If Settings has unsaved changes, choose **Save**, **Discard**, or **Cancel**.

For an administrator session or a read-only folder, download the portable executable from [GitHub Releases](https://github.com/notuxius/keyrewind/releases), choose **Exit** in KeyRewind, and replace the old executable in a folder you can write to. Install v0.4.0 manually once if you're using an older version.

## Terminals and other applications

- Windows Terminal and the classic Windows console paste snippets into the current input line. Use the terminal’s default paste binding. If a target uses a custom paste binding, restore its usual binding to use the shortcut.
- Selecting text in a terminal marks text in its display buffer. The conversion shortcut copies the converted result to the clipboard for pasting where you want it. In writable desktop selections, conversion replaces the selection and restores the previous clipboard content when it can safely preserve it. [Windows Terminal selection](https://learn.microsoft.com/en-us/windows/terminal/selection) · [Windows Terminal copy and paste bindings](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/key-bindings)
- Use the administrator launch when Windows blocks KeyRewind from sending input or layout-change requests to an elevated desktop app. [Windows input restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) · [Windows message restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew)
- KeyRewind edits supported Windows text fields directly. If the clipboard is unavailable, it can also type a short, single-line replacement into a confirmed editable selection while preserving the clipboard.
- Windows restricts keyboard injection into elevated apps. Some apps handle their own text editing, paste, accessibility, and input methods, so compatibility depends on the focused control. For VS Code and Visual Studio, conversion requires a selection the editor exposes through accessibility. [Windows input security](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

Right-click the tray icon and choose **Gaming Mode** to pause shortcuts and release input hooks while KeyRewind stays open. KeyRewind remembers this choice across restarts. Choose **Gaming Mode** again to resume shortcuts. Before launching a protected game, choose **Exit** from the tray menu to close the process and release the hooks. See [playing protected games](GAMING.md).

## Build and check

On Windows 10 or 11, `powershell -ExecutionPolicy Bypass -File .\build.ps1 -Test` builds `KeyRewind.exe` and the `KeyRewind.zip` bundle in `dist`, then runs the smoke tests with the included .NET Framework compiler.
