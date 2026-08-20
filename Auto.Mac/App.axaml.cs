using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ClickyMac.Native;
using ClickyMac.Services;
using ClickyMac.Views;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Services;
using ClickyWindows.Settings;

namespace ClickyMac;

/// <summary>
/// Application entry point -- tray-only, no main window (mirrors the Windows build's
/// invisible-MainWindow-plus-tray-icon shape, just without needing a hidden window to
/// host it since Avalonia's desktop lifetime doesn't require one).
/// </summary>
public partial class App : Application
{
    private AppSettings _settings = new();
    private CompanionManager? _companion;
    private ReplyWindow? _reply;
    private MacHotkeyService? _hotkeys;
    private TrayIcon? _trayIcon;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No main window by design -- don't let closing one shut the app down.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = StartAsync(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Logger.Log("=== Auto (Mac) starting ===");
        _settings = AppSettings.Load();
        Logger.Log($"Settings loaded from: {Logger.LogFilePath.Replace("auto.log", "settings.json")}");

        if (!await ValidateSettingsAsync())
        {
            desktop.Shutdown();
            return;
        }

        _reply = new ReplyWindow();

        IInferenceBackend inference = _settings.UseRemoteServer && !string.IsNullOrWhiteSpace(_settings.RemoteServerUrl)
            ? new RemoteInferenceBackend(_settings.RemoteServerUrl, _settings.RemoteServerToken)
            : new LocalInferenceBackend(_settings);
        Logger.Log($"[Inference] Using {(inference is RemoteInferenceBackend ? $"remote server at {_settings.RemoteServerUrl}" : "local Whisper + direct Claude call")}");

        _companion = new CompanionManager(
            _settings,
            new MacAudioCaptureService(_settings.SystemAudioInputDeviceName),
            new MacScreenCaptureService(),
            inference,
            uiDispatch: a => Dispatcher.UIThread.Invoke(a));

        _companion.TranscriptReady += (transcript, mode) => _reply.ShowTranscript(transcript, mode);
        _companion.ReplyChunkReceived += chunk => _reply.AppendChunk(chunk);
        _companion.ReplyDismissed += () => Dispatcher.UIThread.Invoke(() => _reply.Hide());
        // No overlay/pointing-dot window on Mac yet (see Auto.Mac/README.md) -- Action
        // mode's POINT tags are still parsed and Computer Use still runs, PointReceived
        // just has nothing subscribed to show it on screen. Feedback messages (that would
        // otherwise show as a bubble near the cursor on Windows) go to the log for now.
        _companion.FeedbackReceived += msg => Logger.Log($"[Feedback] {msg}");

        SetupHotkeys();
        SetupTrayIcon();

        var desc = HotkeyDescriptions();
        Logger.Log($"Auto (Mac) ready. Action: {desc.action}, Answer: {desc.answer}, " +
                   $"System Audio: {desc.sysAudio}, Screenshot Q&A: {desc.shot}");
        Logger.Log($"Log file: {Logger.LogFilePath}");
    }

    private async Task<bool> ValidateSettingsAsync()
    {
        if (!string.IsNullOrWhiteSpace(_settings.AnthropicApiKey))
            return true;
        if (_settings.UseRemoteServer && !string.IsNullOrWhiteSpace(_settings.RemoteServerUrl))
            return true; // no local API key needed -- the server holds one

        var dialog = new SettingsWindow(_settings);
        dialog.Show();
        await dialog.WaitForCloseAsync();
        return dialog.Saved;
    }

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_settings);
        dialog.Show();
    }

    private void SetupHotkeys()
    {
        _hotkeys = new MacHotkeyService();

        _hotkeys.AddHotkey(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey,
            onPressed: () => _ = _companion!.OnPushToTalkPressed(InteractionMode.Action),
            onReleased: () => _ = _companion!.OnPushToTalkReleased());

        _hotkeys.AddHotkey(_settings.AnswerHotkeyModifiers, _settings.AnswerHotkeyVirtualKey,
            onPressed: () => _ = _companion!.OnPushToTalkPressed(InteractionMode.Answer),
            onReleased: () => _ = _companion!.OnPushToTalkReleased());

        _hotkeys.AddHotkey(_settings.SystemAudioHotkeyModifiers, _settings.SystemAudioHotkeyVirtualKey,
            onPressed: () => _ = _companion!.OnPushToTalkPressed(InteractionMode.SystemAudio),
            onReleased: () => _ = _companion!.OnPushToTalkReleased());

        _hotkeys.AddHotkey(_settings.ScreenshotQaHotkeyModifiers, _settings.ScreenshotQaHotkeyVirtualKey,
            onPressed: () => _ = _companion!.OnScreenshotQaTriggered(),
            onReleased: () => { });

        _hotkeys.Register();

        foreach (var failed in _hotkeys.FailedHotkeys)
        {
            Logger.Error($"[Hotkey] modifiers=0x{failed.Modifiers:X} keycode=0x{failed.VirtualKey:X} " +
                         $"failed to register (OSStatus {failed.OsStatus}) -- probably claimed by " +
                         "another app. That mode won't respond this run.");
        }
    }

    private void SetupTrayIcon()
    {
        var desc = HotkeyDescriptions();

        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem($"{desc.action}  —  Action (screen shared)") { IsEnabled = false });
        menu.Items.Add(new NativeMenuItem($"{desc.answer}  —  Answer (nothing shared)") { IsEnabled = false });
        menu.Items.Add(new NativeMenuItem($"{desc.sysAudio}  —  System Audio") { IsEnabled = false });
        menu.Items.Add(new NativeMenuItem($"{desc.shot}  —  Screenshot Q&A") { IsEnabled = false });
        menu.Items.Add(new NativeMenuItemSeparator());

        var settingsItem = new NativeMenuItem("Settings...");
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(settingsItem);

        var logItem = new NativeMenuItem("View Log");
        logItem.Click += (_, _) => OpenLog();
        menu.Items.Add(logItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem("Quit Auto");
        quitItem.Click += async (_, _) => await QuitAppAsync();
        menu.Items.Add(quitItem);

        // No custom icon set yet -- see Auto.Mac/README.md for adding one once this
        // can actually be run and the default fallback appearance can be checked.
        _trayIcon = new TrayIcon
        {
            ToolTipText = $"Auto — {desc.action}: action, {desc.answer}: answer, " +
                          $"{desc.sysAudio}: sys audio, {desc.shot}: screen Q&A",
            Menu = menu,
            IsVisible = true,
        };
    }

    private (string action, string answer, string sysAudio, string shot) HotkeyDescriptions() => (
        MacKeyNames.Describe(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey),
        MacKeyNames.Describe(_settings.AnswerHotkeyModifiers, _settings.AnswerHotkeyVirtualKey),
        MacKeyNames.Describe(_settings.SystemAudioHotkeyModifiers, _settings.SystemAudioHotkeyVirtualKey),
        MacKeyNames.Describe(_settings.ScreenshotQaHotkeyModifiers, _settings.ScreenshotQaHotkeyVirtualKey));

    private void OpenLog()
    {
        try { System.Diagnostics.Process.Start("open", ["-t", Logger.LogFilePath]); }
        catch { }
    }

    private async Task QuitAppAsync()
    {
        _hotkeys?.Dispose();
        if (_companion != null)
            await _companion.DisposeAsync();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
