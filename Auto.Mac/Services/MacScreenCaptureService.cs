using System.Diagnostics;
using System.Drawing;
using ClickyWindows.Helpers;
using ClickyWindows.Services;

namespace ClickyMac.Services;

/// <summary>
/// Screenshots via the built-in `screencapture` CLI rather than any capture framework
/// binding -- zero native P/Invoke, zero extra dependency, and it's the same tool macOS's
/// own screenshot shortcuts use, so it already handles Screen Recording permission
/// prompting correctly. Captures the whole desktop (all displays combined) as one image
/// rather than per-display like the Windows build -- simpler, and interview-prep usage is
/// overwhelmingly single-display anyway. Resizing (for the Computer Use pointing pass) goes
/// through `sips`, also built in -- avoids System.Drawing.Bitmap/Graphics, which are
/// Windows-only starting .NET 6 and would throw PlatformNotSupportedException here.
/// UNTESTED: no Mac available to verify screencapture/sips argument behavior firsthand.
/// </summary>
public class MacScreenCaptureService : IScreenCaptureService
{
    private const string ScreencapturePath = "/usr/sbin/screencapture";
    private const string SipsPath = "/usr/bin/sips";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    public List<ScreenshotResult> CaptureAll()
    {
        var tempPath = TempJpegPath();
        try
        {
            // -x: no camera shutter sound. -t jpg: JPEG output (matches the Windows build).
            if (!RunProcess(ScreencapturePath, ["-x", "-t", "jpg", tempPath]))
            {
                Logger.Error("[Screen] screencapture failed -- likely missing Screen Recording permission (System Settings > Privacy & Security > Screen Recording).");
                return [];
            }

            var (w, h) = ReadPixelSize(tempPath);
            var jpeg = File.ReadAllBytes(tempPath);
            var result = new ScreenshotResult(
                jpeg, Convert.ToBase64String(jpeg),
                "Screen capture (all displays)", new Rectangle(0, 0, w, h));
            return [result];
        }
        catch (Exception ex)
        {
            Logger.Error($"[Screen] Capture failed: {ex.Message}");
            return [];
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public ScreenshotResult? CaptureResized(Rectangle bounds, int targetWidth, int targetHeight)
    {
        var tempPath = TempJpegPath();
        try
        {
            if (!RunProcess(ScreencapturePath, ["-x", "-t", "jpg", tempPath]))
                return null;

            // sips -z resizes in place, height then width (its argument order, not a typo).
            RunProcess(SipsPath, ["-z", targetHeight.ToString(), targetWidth.ToString(), tempPath]);

            var jpeg = File.ReadAllBytes(tempPath);
            return new ScreenshotResult(
                jpeg, Convert.ToBase64String(jpeg),
                $"Resized {targetWidth}x{targetHeight}", bounds);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Screen] Resized capture failed: {ex.Message}");
            return null;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static (int w, int h) ReadPixelSize(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(SipsPath) { RedirectStandardOutput = true };
            psi.ArgumentList.Add("-g"); psi.ArgumentList.Add("pixelWidth");
            psi.ArgumentList.Add("-g"); psi.ArgumentList.Add("pixelHeight");
            psi.ArgumentList.Add(path);

            using var p = Process.Start(psi);
            if (p == null) return (0, 0);
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit((int)ProcessTimeout.TotalMilliseconds);

            int w = 0, h = 0;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("pixelWidth:"))
                    int.TryParse(trimmed["pixelWidth:".Length..].Trim(), out w);
                else if (trimmed.StartsWith("pixelHeight:"))
                    int.TryParse(trimmed["pixelHeight:".Length..].Trim(), out h);
            }
            return (w, h);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Screen] Could not read captured image size: {ex.Message}");
            return (0, 0);
        }
    }

    private static bool RunProcess(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit((int)ProcessTimeout.TotalMilliseconds);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Screen] Failed to run {exe}: {ex.Message}");
            return false;
        }
    }

    private static string TempJpegPath() => Path.Combine(Path.GetTempPath(), $"clicky-shot-{Guid.NewGuid():N}.jpg");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
