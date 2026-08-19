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

        _mainWindow = new MainWindow(_settings, _companion, _overlay);
        MainWindow = _mainWindow;
        _mainWindow.Show();

        SetupTrayIcon();

        Logger.Log($"Clicky ready. Hotkey: {GetHotkeyDescription()}");
        Logger.Log($"Log file: {Logger.LogFilePath}");
        ShowBalloon(
            $"Clicky ready! Hold {GetHotkeyDescription()} to talk. First reply will pause a few " +
            "seconds while the speech model loads.", ToolTipIcon.Info);
    }

    private bool ValidateSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.AnthropicApiKey) &&
            string.IsNullOrWhiteSpace(_settings.ClaudeProxyUrl))
        {
            _settings.Save();
            var path = Logger.LogFilePath.Replace("clicky.log", "settings.json");
            System.Windows.MessageBox.Show(
                $"Please add your API key to:\n{path}\n\n" +
                "Required field:\n• AnthropicApiKey\n\n" +
                "(Speech-to-text and \"speech\" output are fully local in this build — " +
                "no ElevenLabs or AssemblyAI keys needed.)",
                "Clicky — Setup Required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
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

        var hotkeyDesc = GetHotkeyDescription();
        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = $"Clicky (local) — Hold {hotkeyDesc} to talk",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add($"Clicky (local)  |  Hold {hotkeyDesc} to talk").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("View Log", null, (_, _) => OpenLog());
        menu.Items.Add("Open Settings", null, (_, _) => OpenSettingsFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Clicky", null, (_, _) => QuitApp());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => OpenLog();
    }

    private string GetHotkeyDescription()
    {
        var parts = new List<string>();
        if ((_settings.HotkeyModifiers & 0x0004) != 0) parts.Add("Shift");
        if ((_settings.HotkeyModifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((_settings.HotkeyModifiers & 0x0001) != 0) parts.Add("Alt");
        if ((_settings.HotkeyModifiers & 0x0008) != 0) parts.Add("Win");
        parts.Add(_settings.HotkeyVirtualKey == 0x20 ? "Space" : $"Key(0x{_settings.HotkeyVirtualKey:X})");
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

    private void OpenSettingsFolder()
    {
        var dir = Path.GetDirectoryName(Logger.LogFilePath)!;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
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
