using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using ClickyWindows.Helpers;
using ClickyWindows.Services;
using ClickyWindows.Settings;

namespace ClickyWindows;

/// <summary>
/// Application entry point. Owns the system tray icon and coordinates startup.
/// </summary>
public partial class App
{
    private NotifyIcon? _trayIcon;
    private CompanionManager? _companion;
    private OverlayWindow? _overlay;
    private ReplyWindow? _reply;
    private MainWindow? _mainWindow;
    private AppSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Wire Logger to tray balloon tips
        Logger.OnError += msg => Dispatcher.Invoke(() => ShowBalloon(msg, ToolTipIcon.Error));
        Logger.OnInfo  += msg => Dispatcher.Invoke(() => ShowBalloon(msg, ToolTipIcon.Info));

        Logger.Log("=== Clicky (local) starting ===");

        // Single-instance guard
        var mutex = new System.Threading.Mutex(true, "ClickyWindowsLocal_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            System.Windows.MessageBox.Show(
                "Clicky is already running. Check the system tray.",
                "Clicky", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        Logger.Log($"Settings loaded from: {Logger.LogFilePath.Replace("clicky.log", "settings.json")}");

        if (!ValidateSettings())
        {
            Shutdown();
            return;
        }

        _companion = new CompanionManager(_settings);

        _overlay = new OverlayWindow();
        _overlay.Show();

        _reply = new ReplyWindow();

        _mainWindow = new MainWindow(_settings, _companion, _overlay, _reply);
        MainWindow = _mainWindow;
        _mainWindow.Show();

        SetupTrayIcon();

        Logger.Log($"Clicky ready. Action: {GetActionHotkeyDescription()}, Answer: {GetAnswerHotkeyDescription()}, " +
                   $"System Audio: {GetSystemAudioHotkeyDescription()}");
        Logger.Log($"Log file: {Logger.LogFilePath}");
        ShowBalloon(
            $"Clicky ready! Hold {GetActionHotkeyDescription()} to talk with your screen shared, " +
            $"{GetAnswerHotkeyDescription()} for a plain answer with nothing shared, or " +
            $"{GetSystemAudioHotkeyDescription()} to react to whatever's playing through your speakers. " +
            "First reply will pause a few seconds while the speech model loads.", ToolTipIcon.Info);
    }

    private bool ValidateSettings()
    {
        if (!string.IsNullOrWhiteSpace(_settings.AnthropicApiKey))
            return true;

        // First run (or key was cleared) — open the settings window instead of pointing
        // at a JSON file. Blocking: nothing starts until a key is entered or the user quits.
        var dialog = new SettingsWindow(_settings);
        var result = dialog.ShowDialog();
        return result == true && dialog.Saved;
    }

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_settings);
        if (dialog.ShowDialog() == true && dialog.Saved)
        {
            ShowBalloon("Settings saved.", ToolTipIcon.Info);
        }
    }

    private void SetupTrayIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.FillEllipse(new SolidBrush(System.Drawing.Color.FromArgb(0x25, 0x63, 0xEB)), 1, 1, 14, 14);
            g.DrawEllipse(new Pen(System.Drawing.Color.White, 1.5f), 2, 2, 12, 12);
        }
        var icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());

        var actionDesc = GetActionHotkeyDescription();
        var answerDesc = GetAnswerHotkeyDescription();
        var systemAudioDesc = GetSystemAudioHotkeyDescription();
        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = $"Clicky (local) — {actionDesc}: action, {answerDesc}: answer, {systemAudioDesc}: system audio",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add($"{actionDesc}  |  Action mode (screen shared)").Enabled = false;
        menu.Items.Add($"{answerDesc}  |  Answer mode (nothing shared)").Enabled = false;
        menu.Items.Add($"{systemAudioDesc}  |  System Audio mode (listens to speakers, not mic)").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("View Log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Clicky", null, (_, _) => QuitApp());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => OpenLog();
    }

    private string GetActionHotkeyDescription() => DescribeHotkey(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
    private string GetAnswerHotkeyDescription() => DescribeHotkey(_settings.AnswerHotkeyModifiers, _settings.AnswerHotkeyVirtualKey);
    private string GetSystemAudioHotkeyDescription() => DescribeHotkey(_settings.SystemAudioHotkeyModifiers, _settings.SystemAudioHotkeyVirtualKey);

    private static string DescribeHotkey(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0008) != 0) parts.Add("Win");
        parts.Add(virtualKey switch
        {
            0x20 => "Space",
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(), // 'A'-'Z'
            _ => $"Key(0x{virtualKey:X})",
        });
        return string.Join("+", parts);
    }

    private void ShowBalloon(string message, ToolTipIcon icon)
    {
        _trayIcon?.ShowBalloonTip(5000, "Clicky", message, icon);
    }

    private void OpenLog()
    {
        try { System.Diagnostics.Process.Start("notepad.exe", Logger.LogFilePath); }
        catch { }
    }

    private async void QuitApp()
    {
        _trayIcon?.Dispose();
        if (_companion != null)
            await _companion.DisposeAsync();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
