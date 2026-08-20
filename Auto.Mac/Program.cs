using Avalonia;
using ClickyMac.Native;
using ClickyWindows.Helpers;
using ClickyWindows.Settings;

namespace ClickyMac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must happen before anything in Auto.Core touches disk (Logger, AppSettings,
        // LocalWhisperService) -- otherwise it'd fall back to the Windows build's "Auto"
        // folder name and the two apps would collide over one settings.json / model
        // cache / log file when both are built and run on the same machine (see AppPaths).
        AppPaths.AppFolderName = "AutoMac";

        bool firstRun = !AppSettings.Exists();
        if (firstRun)
        {
            var fresh = AppSettings.Load(); // returns defaults; nothing on disk yet
            ApplyMacHotkeyDefaults(fresh);
            fresh.Save();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // AppSettings' compiled-in defaults are Win32 codes (see the comment on those fields) --
    // on a brand-new Mac install (no settings.json yet) they need to be overwritten with
    // Carbon modifier masks and macOS virtual keycodes before first save, or the Mac hotkey
    // service would try to register nonsense values. Once settings.json exists, whatever's
    // in it (including if the user edits it) is trusted as-is, same as the Windows build.
    private static void ApplyMacHotkeyDefaults(AppSettings s)
    {
        const uint ctrlShift = Carbon.controlKey | Carbon.shiftKey;
        // Deliberately Control+Shift, not Cmd+Shift: Cmd+Shift+3/4 are macOS's own
        // screenshot shortcuts system-wide -- reusing them would be a bad, confusing
        // conflict on every Mac, not just this app.
        s.HotkeyModifiers = ctrlShift;              s.HotkeyVirtualKey = 0x12;             // kVK_ANSI_1
        s.AnswerHotkeyModifiers = ctrlShift;         s.AnswerHotkeyVirtualKey = 0x13;       // kVK_ANSI_2
        s.SystemAudioHotkeyModifiers = ctrlShift;    s.SystemAudioHotkeyVirtualKey = 0x14;  // kVK_ANSI_3
        s.ScreenshotQaHotkeyModifiers = ctrlShift;   s.ScreenshotQaHotkeyVirtualKey = 0x15; // kVK_ANSI_4
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
