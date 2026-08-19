using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using Screen = System.Windows.Forms.Screen;

namespace ClickyWindows;

/// <summary>
/// Small always-on-top panel, bottom-right corner, that streams Claude's reply in real
/// time as chunks arrive from CompanionManager.ReplyChunkReceived. Replaces speaking the
/// reply aloud (SapiTtsService) — read instead of heard, updates live instead of waiting
/// for the full response.
/// </summary>
public partial class ReplyWindow : Window
{
    private const double PanelMargin = 24;

    public ReplyWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
            Logger.Error("[Reply] SetWindowDisplayAffinity failed -- reply panel will be visible in screen shares (needs Windows 10 2004+).");
    }

    /// <summary>
    /// Opens the panel (clearing any previous reply) as soon as Whisper produces a
    /// transcript -- shown immediately, before Claude has even started replying, so a
    /// mis-transcription is visible right away instead of only after a confusing answer.
    /// [mode] labels the panel with which hotkey produced this turn.
    /// </summary>
    public void ShowTranscript(string transcript, InteractionMode mode)
    {
        Dispatcher.Invoke(() =>
        {
            TranscriptText.Text = $"“{transcript}”";
            ReplyText.Text = "";
            var (label, colorHex) = mode switch
            {
                InteractionMode.Action => ("Clicky — Action", "#7C8AFF"),       // periwinkle, matches original accent
                InteractionMode.Answer => ("Clicky — Answer", "#4CD99C"),       // green — no screen shared this turn
                InteractionMode.SystemAudio => ("Clicky — System Audio", "#FFB347"), // amber — listening to speakers, not the mic
                InteractionMode.ScreenshotQA => ("Clicky — Screen Q&A", "#C77DFF"),  // violet — one-shot, answers everything visible
                _ => ("Clicky", "#7C8AFF"),
            };
            ModeLabel.Text = label;
            ModeLabel.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));

            // Show() before positioning: DPI detection needs a live PresentationSource,
            // which doesn't exist until the window has been shown at least once. Positioning
            // first (as this used to) silently fell back to 1.0x DPI on any scaled display,
            // pushing the panel off-screen on anything other than 100% scaling.
            Show();
            PositionTopRight();
            Activate();
        });
    }

    /// <summary>Appends [chunk] to the visible reply text.</summary>
    public void AppendChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        Dispatcher.Invoke(() => ReplyText.Text += chunk);
    }

    // Fixed top-right corner — grows downward as text streams in, capped by MaxHeight
    // (see XAML) with an internal ScrollViewer past that, so it can never spill off
    // the bottom of the screen the way bottom-anchoring (growing upward) could.
    private void PositionTopRight()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var dpi = VisualTreeHelper2.GetDpiScale(this);
        Left = area.Right / dpi - Width - PanelMargin;
        Top = area.Top / dpi + PanelMargin;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();
}

/// <summary>Minimal DPI helper — avoids pulling in a full CoordinateHelper dependency here.</summary>
internal static class VisualTreeHelper2
{
    public static double GetDpiScale(Window window)
    {
        var source = System.Windows.PresentationSource.FromVisual(window);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }
}
