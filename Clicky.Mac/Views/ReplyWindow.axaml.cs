using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClickyMac.Native;
using ClickyWindows.Models;

namespace ClickyMac.Views;

/// <summary>
/// Avalonia port of the WPF ReplyWindow: a small always-on-top, borderless panel,
/// top-right corner, streaming Claude's reply in as chunks arrive. Same behavior,
/// same layout/colors -- see ReplyWindow.xaml.cs in the Windows project for the
/// original this mirrors.
/// </summary>
public partial class ReplyWindow : Window
{
    private const double PanelMargin = 24;

    public ReplyWindow()
    {
        InitializeComponent();
        Opened += (_, _) => WindowCapture.ExcludeFromCapture(this);
    }

    /// <summary>
    /// Opens the panel (clearing any previous reply) as soon as Whisper produces a
    /// transcript -- shown immediately, before Claude has even started replying, so a
    /// mis-transcription is visible right away instead of only after a confusing answer.
    /// [mode] labels the panel with which hotkey produced this turn.
    /// </summary>
    public void ShowTranscript(string transcript, InteractionMode mode)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            TranscriptText.Text = $"“{transcript}”";
            ReplyText.Text = "";

            var (label, colorHex) = mode switch
            {
                InteractionMode.Action => ("Auto — Action", "#7C8AFF"),
                InteractionMode.Answer => ("Auto — Answer", "#4CD99C"),
                InteractionMode.SystemAudio => ("Auto — System Audio", "#FFB347"),
                InteractionMode.ScreenshotQA => ("Auto — Screen Q&A", "#C77DFF"),
                _ => ("Auto", "#7C8AFF"),
            };
            ModeLabel.Text = label;
            ModeLabel.Foreground = new SolidColorBrush(Color.Parse(colorHex));

            Show();
            PositionTopRight();
            Activate();
        });
    }

    /// <summary>Appends [chunk] to the visible reply text.</summary>
    public void AppendChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        Dispatcher.UIThread.Invoke(() => ReplyText.Text += chunk);
    }

    // Fixed top-right corner — grows downward as text streams in, capped by MaxHeight
    // (see XAML) with an internal ScrollViewer past that.
    private void PositionTopRight()
    {
        var screen = Screens.Primary;
        if (screen == null) return;

        var area = screen.WorkingArea; // physical pixels
        var scaling = screen.Scaling;
        int marginPx = (int)(PanelMargin * scaling);
        int widthPx = (int)(Width * scaling); // Width is fixed (460 DIP); only height auto-sizes

        Position = new PixelPoint(area.Right - widthPx - marginPx, area.Y + marginPx);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Hide();
}
