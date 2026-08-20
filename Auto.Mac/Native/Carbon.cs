using System.Runtime.InteropServices;

namespace ClickyMac.Native;

/// <summary>
/// P/Invoke bindings for the small slice of the Carbon Event Manager still used for
/// global hotkeys — RegisterEventHotKey has no modern replacement and is what nearly
/// every macOS hotkey utility (this codebase included, conceptually) still relies on;
/// Carbon itself is deprecated for UI, but this specific API keeps working on current
/// macOS. UNTESTED: written from documented signatures with no Mac available to verify
/// against — see Auto.Mac/README.md for what to check first if hotkeys don't fire.
/// </summary>
internal static class Carbon
{
    private const string CarbonLib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    [StructLayout(LayoutKind.Sequential)]
    public struct EventHotKeyID
    {
        public uint signature;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTypeSpec
    {
        public uint eventClass;
        public uint eventKind;
    }

    // FourCC constants, written as their big-endian uint32 value: 'keyb', 'clik' (our own
    // signature for EventHotKeyID so we never collide with another app's), '----', 'hkid'.
    public const uint kEventClassKeyboard = 0x6B657962;
    public const uint kEventHotKeyPressed = 5;
    public const uint kEventHotKeyReleased = 6;
    public const uint kEventParamDirectObject = 0x2D2D2D2D;
    public const uint typeEventHotKeyID = 0x686B6964;
    public const uint ClickySignature = 0x636C696B; // 'clik'

    // Carbon modifier masks (distinct bit layout from Win32's MOD_* -- do not mix the two).
    public const uint cmdKey = 0x0100;
    public const uint shiftKey = 0x0200;
    public const uint optionKey = 0x0800;
    public const uint controlKey = 0x1000;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int EventHandlerProc(IntPtr inHandlerCallRef, IntPtr inEvent, IntPtr inUserData);

    [DllImport(CarbonLib)]
    public static extern int RegisterEventHotKey(
        uint inHotKeyCode, uint inHotKeyModifiers, EventHotKeyID inHotKeyID,
        IntPtr inTarget, uint inOptions, out IntPtr outRef);

    [DllImport(CarbonLib)]
    public static extern int UnregisterEventHotKey(IntPtr inHotKey);

    [DllImport(CarbonLib)]
    public static extern IntPtr GetApplicationEventTarget();

    [DllImport(CarbonLib)]
    public static extern IntPtr NewEventHandlerUPP(EventHandlerProc userRoutine);

    [DllImport(CarbonLib)]
    public static extern int InstallEventHandler(
        IntPtr inTarget, IntPtr inHandler, int inNumTypes,
        [In] EventTypeSpec[] inList, IntPtr inUserData, out IntPtr outRef);

    [DllImport(CarbonLib)]
    public static extern int RemoveEventHandler(IntPtr inHandler);

    [DllImport(CarbonLib)]
    public static extern int GetEventParameter(
        IntPtr inEvent, uint inName, uint inDesiredType,
        out uint outActualType, int inBufferSize, out int outActualSize, out EventHotKeyID outData);

    [DllImport(CarbonLib)]
    public static extern uint GetEventKind(IntPtr inEvent);
}
