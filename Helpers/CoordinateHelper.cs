using System.Windows.Forms;

namespace ClickyWindows.Helpers;

/// <summary>
/// Converts coordinates between coordinate spaces:
/// - Physical pixels (Win32 / screen capture)
/// - WPF device-independent units (96 DPI base)
/// - Claude Computer Use resolution (1024x768)
/// </summary>
internal static class CoordinateHelper
{
    // Claude Computer Use returns coordinates in one of these standard resolutions
    private static readonly (int w, int h)[] ComputerUseResolutions =
    [
        (1024, 768),
        (1280, 800),
        (1366, 768),
    ];

    /// <summary>
    /// Gets the DPI scale factor for the primary screen (physical pixels / WPF DIPs).
    /// </summary>
    public static double GetDpiScale()
    {
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }

    /// <summary>
    /// Converts physical pixel coordinates (from screen capture or Win32) to
    /// WPF device-independent units, returned as (x, y) doubles.
    /// </summary>
    public static (double x, double y) PhysicalToWpf(int physX, int physY, double dpiScale)
        => (physX / dpiScale, physY / dpiScale);

    /// <summary>
    /// Converts WPF device-independent units to physical pixel coordinates.
    /// </summary>
    public static (int x, int y) WpfToPhysical(double wpfX, double wpfY, double dpiScale)
        => ((int)(wpfX * dpiScale), (int)(wpfY * dpiScale));

    /// <summary>
    /// Scales Claude Computer Use coordinates (relative to a standard resolution)
    /// to physical screen pixels, then to WPF DIPs.
    /// Returns (wpfX, wpfY).
    /// </summary>
    public static (double x, double y) ComputerUseToWpf(
        int cuX, int cuY,
        int screenWidth, int screenHeight,
        double dpiScale)
    {
        var (cuW, cuH) = DetectComputerUseResolution(screenWidth, screenHeight);

        cuX = Math.Clamp(cuX, 0, cuW - 1);
        cuY = Math.Clamp(cuY, 0, cuH - 1);

        double physX = (double)cuX / cuW * screenWidth;
        double physY = (double)cuY / cuH * screenHeight;

        return (physX / dpiScale, physY / dpiScale);
    }

    internal static (int w, int h) DetectComputerUseResolution(int screenW, int screenH)
    {
        double screenRatio = (double)screenW / screenH;
        (int w, int h) best = ComputerUseResolutions[0];
        double bestDiff = double.MaxValue;

        foreach (var res in ComputerUseResolutions)
        {
            double ratio = (double)res.w / res.h;
            double diff = Math.Abs(ratio - screenRatio);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = res;
            }
        }

        return best;
    }
}
