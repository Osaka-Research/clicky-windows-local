using System.Windows;
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
    }

    /// <summary>Clears any previous reply and shows the panel, anchored top-right.</summary>
    public void BeginReply()
    {
        Dispatcher.Invoke(() =>
        {
            ReplyText.Text = "";
            PositionTopRight();
            Show();
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
