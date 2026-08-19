using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

public record ScreenshotResult(
    byte[] JpegBytes,      // raw JPEG data
    string Base64,         // base64-encoded JPEG for Claude API
    string Label,          // human-readable label (e.g., "Primary display — cursor here at (1234, 567)")
    Rectangle Bounds       // physical pixel bounds of the captured screen
);

/// <summary>
/// Captures JPEG screenshots of all connected monitors using GDI (System.Drawing).
/// Reliable, no extra packages required, produces JPEG at quality 80 (matching macOS Clicky).
/// The monitor where the cursor is located is returned first.
/// </summary>
public class ScreenCaptureService
{
    private const long JpegQuality = 80L;

    public List<ScreenshotResult> CaptureAll()
    {
        Win32.GetCursorPos(out var cursorPos);

        var results = new List<ScreenshotResult>();
        var screens = Screen.AllScreens;

        // Sort: screen containing cursor comes first
        var sorted = screens
            .OrderByDescending(s => s.Bounds.Contains(cursorPos.X, cursorPos.Y))
            .ToList();

        foreach (var screen in sorted)
        {
            var result = CaptureScreen(screen, cursorPos);
            if (result != null)
                results.Add(result);
        }

        return results;
    }

    private static ScreenshotResult? CaptureScreen(Screen screen, Win32.POINT cursorPos)
    {
        try
        {
            var bounds = screen.Bounds;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            }

            bool hasCursor = bounds.Contains(cursorPos.X, cursorPos.Y);
            string label = hasCursor
                ? $"Display '{screen.DeviceName}' (primary display with cursor at physical pixel ({cursorPos.X}, {cursorPos.Y}))"
                : $"Display '{screen.DeviceName}' (secondary display, no cursor)";

            byte[] jpeg = EncodeJpeg(bitmap);
            string base64 = Convert.ToBase64String(jpeg);

            return new ScreenshotResult(jpeg, base64, label, bounds);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Captures a specific screen region and resizes it to the given dimensions.
    /// Used to produce a Computer Use-compatible screenshot at the expected CU resolution.
    /// </summary>
    public ScreenshotResult? CaptureResized(System.Drawing.Rectangle bounds, int targetWidth, int targetHeight)
    {
        try
        {
            using var fullBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(fullBitmap))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

            using var resized = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(fullBitmap, 0, 0, targetWidth, targetHeight);
            }

            byte[] jpeg = EncodeJpeg(resized);
            string base64 = Convert.ToBase64String(jpeg);
            return new ScreenshotResult(jpeg, base64, $"Resized {targetWidth}x{targetHeight}", bounds);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] EncodeJpeg(Bitmap bitmap)
    {
        var jpegEncoder = GetEncoder(ImageFormat.Jpeg)!;
        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, JpegQuality);

        using var ms = new MemoryStream();
        bitmap.Save(ms, jpegEncoder, encoderParams);
        return ms.ToArray();
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        return ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == format.Guid);
    }
}
