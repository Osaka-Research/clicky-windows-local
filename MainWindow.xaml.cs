using System.Windows;
using ClickyWindows.Models;
using ClickyWindows.Services;
using ClickyWindows.Settings;

namespace ClickyWindows;

/// <summary>
/// Invisible message-pump window. Hosts the HotkeyService (requires an HWND)
/// and drives CompanionManager.
/// </summary>
public partial class MainWindow : Window
{
    private readonly HotkeyService _hotkey;
    private readonly CompanionManager _companion;
    private readonly OverlayWindow _overlay;
    private readonly ReplyWindow _reply;
    private readonly AppSettings _settings;

    public MainWindow(AppSettings settings, CompanionManager companion, OverlayWindow overlay, ReplyWindow reply)
    {
        InitializeComponent();

        _settings = settings;
        _companion = companion;
        _overlay = overlay;
        _reply = reply;
        _hotkey = new HotkeyService();

        // Action mode: screenshot captured and sent, Claude can point at something on screen.
        _hotkey.AddHotkey(
            settings.HotkeyModifiers, settings.HotkeyVirtualKey,
            onPressed: () => _ = _companion.OnPushToTalkPressed(InteractionMode.Action),
            onReleased: () => _ = _companion.OnPushToTalkReleased());

        // Answer mode: no screenshot at all, pure Q&A.
        _hotkey.AddHotkey(
            settings.AnswerHotkeyModifiers, settings.AnswerHotkeyVirtualKey,
            onPressed: () => _ = _companion.OnPushToTalkPressed(InteractionMode.Answer),
            onReleased: () => _ = _companion.OnPushToTalkReleased());

        // System Audio mode: listens to whatever's playing through speakers (WASAPI
        // loopback) instead of the mic, no screenshot -- react to a call/video.
        _hotkey.AddHotkey(
            settings.SystemAudioHotkeyModifiers, settings.SystemAudioHotkeyVirtualKey,
            onPressed: () => _ = _companion.OnPushToTalkPressed(InteractionMode.SystemAudio),
            onReleased: () => _ = _companion.OnPushToTalkReleased());

        // Screenshot Q&A: fires on press alone, no hold/release, no mic -- one screenshot,
        // answer everything visible in it.
        _hotkey.AddHotkey(
            settings.ScreenshotQaHotkeyModifiers, settings.ScreenshotQaHotkeyVirtualKey,
            onPressed: () => _ = _companion.OnScreenshotQaTriggered(),
            onReleased: () => { });

        // Wire companion events to overlay
        _companion.StateChanged += state =>
            Dispatcher.Invoke(() => _overlay.SetState(state));

        _companion.PointReceived += (x, y, label) =>
            Dispatcher.Invoke(() => _overlay.ShowTargetAt(x, y, label));

        _companion.AudioLevelChanged += level =>
            Dispatcher.Invoke(() => _overlay.SetAudioLevel(level));

        _companion.FeedbackReceived += msg =>
            Dispatcher.Invoke(() => _overlay.ShowFeedback(msg));

        _companion.TranscriptConfirmed +=
            () => Dispatcher.Invoke(() => _overlay.PulseSpinner());

        // Wire companion events to the live reply panel
        _companion.TranscriptReady += (transcript, mode) => _reply.ShowTranscript(transcript, mode);
        _companion.ReplyChunkReceived += chunk => _reply.AppendChunk(chunk);
        _companion.ReplyDismissed += () => Dispatcher.Invoke(() => _reply.Hide());

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hotkey.Register(this);

        // A hotkey failing to register (most likely claimed by another running app) used
        // to crash the whole app before it even showed a window. Now it just leaves that
        // one mode unavailable -- surface it via the same feedback bubble used for other
        // silent failures, so it's not just a log line no one will see.
        foreach (var failed in _hotkey.FailedHotkeys)
        {
            var desc = ClickyWindows.Helpers.Win32.DescribeHotkey(failed.Modifiers, failed.VirtualKey);
            _overlay.ShowFeedback($"{desc} is already used by another app — that mode won't respond");
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _hotkey.Dispose();
    }
}
