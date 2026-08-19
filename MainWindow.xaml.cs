using System.Windows;
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

    public MainWindow(AppSettings settings, CompanionManager companion, OverlayWindow overlay, ReplyWindow reply)
    {
        InitializeComponent();

        _companion = companion;
        _overlay = overlay;
        _reply = reply;
        _hotkey = new HotkeyService(settings.HotkeyModifiers, settings.HotkeyVirtualKey);

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
        _companion.ReplyStarted += () => _reply.BeginReply();
        _companion.ReplyChunkReceived += chunk => _reply.AppendChunk(chunk);
        _companion.ReplyDismissed += () => Dispatcher.Invoke(() => _reply.Hide());

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hotkey.Register(this);
        _hotkey.PushToTalkPressed  += () => _ = _companion.OnPushToTalkPressed();
        _hotkey.PushToTalkReleased += () => _ = _companion.OnPushToTalkReleased();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _hotkey.Dispose();
    }
}
