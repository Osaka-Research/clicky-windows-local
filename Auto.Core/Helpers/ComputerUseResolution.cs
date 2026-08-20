namespace ClickyWindows.Helpers;

/// <summary>
/// The portable half of coordinate handling: which of Claude Computer Use's standard
/// resolutions best matches a given screen's aspect ratio. Physical-pixel/DIP/DPI
/// conversions stay platform-specific (WPF vs Avalonia units differ) and live in each
/// platform project's own CoordinateHelper.
/// </summary>
public static class ComputerUseResolution
{
    // Claude Computer Use returns coordinates in one of these standard resolutions
    private static readonly (int w, int h)[] Resolutions =
    [
        (1024, 768),
        (1280, 800),
        (1366, 768),
    ];

    public static (int w, int h) Detect(int screenW, int screenH)
    {
        double screenRatio = (double)screenW / screenH;
        (int w, int h) best = Resolutions[0];
        double bestDiff = double.MaxValue;

        foreach (var res in Resolutions)
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
