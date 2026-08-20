using System.Runtime.InteropServices;
using Avalonia.Controls;
using Auto.Helpers;

namespace Auto.Mac.Native;

/// <summary>
/// macOS's actual equivalent of Windows' SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE):
/// NSWindow.sharingType = .none. Simpler than the Windows path -- it's a plain AppKit
/// window property, not a separate capture-exclusion API -- reached via a raw objc_msgSend
/// call since Avalonia doesn't expose it directly. UNTESTED: no Mac available to verify;
/// this is the standard, widely-used pattern for calling into AppKit from managed code
/// (the same mechanism Avalonia's own macOS backend and Xamarin.Mac are built on), but
/// hasn't been run.
/// </summary>
internal static class WindowCapture
{
    private const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    // objc_msgSend is variadic in C, but from P/Invoke each distinct signature needs its
    // own extern declaration matching the actual call being made -- this one is
    // "- (void)setSharingType:(NSWindowSharingType)type", i.e. a single integer argument
    // and no return value. Safe on both x86_64 and arm64 (no struct return, no float arg,
    // so no objc_msgSend_stret/_fpret variant needed).
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_SetLong(IntPtr receiver, IntPtr selector, nint arg);

    private const nint NSWindowSharingNone = 0;

    /// <summary>
    /// Excludes [window] from screen capture/recording (Zoom, Teams, Meet, OBS, the
    /// built-in Screenshot app's "Record Selected Window", ScreenCaptureKit-based capture)
    /// while it stays fully visible locally. Call after the window's native handle exists
    /// (i.e. after Show(), same ordering constraint as the Windows build's DPI lookup).
    /// </summary>
    public static void ExcludeFromCapture(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle == null || handle.HandleDescriptor != "NSWindow")
        {
            Logger.Error("[Capture] Could not get an NSWindow handle -- panel will be visible in screen shares.");
            return;
        }

        var sel = sel_registerName("setSharingType:");
        objc_msgSend_SetLong(handle.Handle, sel, NSWindowSharingNone);
    }
}
