# KeyRewind

KeyRewind is a Windows tray utility for correcting text typed in the wrong keyboard layout and expanding short abbreviations.

## Use it

1. Download `KeyRewind.exe` from [GitHub Releases](https://github.com/notuxius/keyrewind/releases). If Mahou is running, exit it from its tray icon. Run `KeyRewind.exe` as a standard user. KeyRewind opens Settings on first launch.
2. Select editable text and press **Shift + Alt + Z**. KeyRewind converts the physical keystrokes between your two keyboard layouts and switches to the resulting layout. For example, `ghbdsn` becomes `привіт` with English and Ukrainian.
3. In **Settings**, record either shortcut, or use **Double Space** for snippets. Add an abbreviation and its single-line replacement on the **Snippets** page and save. To reopen Settings while KeyRewind is running, left-click the tray icon or launch `KeyRewind.exe` again.
4. Type an abbreviation by itself and tap **Space** twice within about two-thirds of a second. KeyRewind replaces the abbreviation and consumes both spaces. With a custom snippet shortcut, press the shortcut immediately after typing the abbreviation.

When Windows has exactly two keyboard layouts, KeyRewind uses both. With more than two, choose the pair in **Settings → Shortcuts → Autoswitch layouts**. The conversion shortcut runs when one of those layouts is active. Snippet abbreviations are case-sensitive. Settings are saved locally in `%LOCALAPPDATA%\KeyRewind\settings.json`.

When conversion switches your keyboard layout, KeyRewind shows the new layout in a Windows notification. To turn these off, clear **Show autoswitch popups** under **Settings → Shortcuts → Autoswitch layouts** and select **Save changes**.

In **Settings → Appearance**, choose **System**, **Light**, or **Dark**, then select **Save changes**. System follows the Windows app color setting.

In **Settings → General**, enable **Launch in background** to start KeyRewind with only the tray icon visible. **Close to tray** is enabled by default. The first time you close Settings to the tray, a notification tells you KeyRewind is still running in the background. Turn off **Close to tray** to exit when the window closes. Select **Save changes** to apply your choices.

## Terminals and other applications

- Windows Terminal and the classic Windows console paste snippets into the current input line. Use the terminal’s default paste binding. If a target uses a custom paste binding, restore its usual binding to use the shortcut.
- Selecting text in a terminal marks text in its display buffer. The conversion shortcut copies the converted result to the clipboard for pasting where you want it. In writable desktop selections, conversion replaces the selection and restores the previous clipboard content when it can safely preserve it. [Windows Terminal selection](https://learn.microsoft.com/en-us/windows/terminal/selection) · [Windows Terminal copy and paste bindings](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/key-bindings)
- KeyRewind edits supported Windows text fields directly. If the clipboard is unavailable, it can also type a short, single-line replacement into a confirmed editable selection while preserving the clipboard.
- Windows restricts keyboard injection into elevated apps. Some apps handle their own text editing, paste, accessibility, and input methods, so compatibility depends on the focused control. For VS Code and Visual Studio, conversion requires a selection the editor exposes through accessibility. [Windows input security](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

Right-click the tray icon and choose **Gaming Mode** to pause shortcuts and release input hooks while KeyRewind stays open. KeyRewind remembers this choice across restarts. Choose **Gaming Mode** again to resume shortcuts. Before launching a protected game, choose **Exit** from the tray menu to close the process and release the hooks. See [playing protected games](GAMING.md).

## Build and check

On Windows 10 or 11, `powershell -ExecutionPolicy Bypass -File .\build.ps1 -Test` builds the portable executable and runs the smoke tests with the included .NET Framework compiler.
