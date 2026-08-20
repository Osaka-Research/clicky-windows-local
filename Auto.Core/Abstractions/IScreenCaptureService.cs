using System.Drawing;

namespace ClickyWindows.Services;

/// <summary>
/// Captures JPEG screenshots. Windows implements this over GDI (System.Drawing);
/// macOS shells out to the built-in `screencapture` CLI. CompanionManager only ever
/// talks to this interface.
/// </summary>
public interface IScreenCaptureService
{
    /// <summary>Captures every connected display, screen containing the cursor first.</summary>
    List<ScreenshotResult> CaptureAll();

    /// <summary>
    /// Captures [bounds] and resizes to [targetWidth]x[targetHeight] -- used to produce
    /// a Claude Computer Use-compatible screenshot at the resolution it expects.
    /// </summary>
    ScreenshotResult? CaptureResized(Rectangle bounds, int targetWidth, int targetHeight);
}
