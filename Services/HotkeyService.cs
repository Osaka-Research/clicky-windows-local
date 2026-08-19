using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// Registers a global push-to-talk hotkey using Win32 RegisterHotKey.
/// Non-intercepting: the key event still reaches all other applications.
/// Fires PushToTalkPressed / PushToTalkReleased events.
/// </summary>
public class HotkeyService : IDisposable
{
    private const int HotkeyId = 9001;

    private readonly uint _modifiers;
    private readonly uint _virtualKey;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private bool _isPressed;

    public event Action? PushToTalkPressed;
    public event Action? PushToTalkReleased;

    public HotkeyService(uint modifiers, uint virtualKey)
    {
        _modifiers = modifiers | Win32.MOD_NOREPEAT; // suppress key-repeat while held
        _virtualKey = virtualKey;
    }

    /// <summary>
    /// Must be called after the window's HWND is available (after SourceInitialized).
    /// </summary>
    public void Register(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.Handle;

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        bool ok = Win32.RegisterHotKey(_hwnd, HotkeyId, _modifiers, _virtualKey);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"RegisterHotKey failed (error {err}). Try a different hotkey combination.");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            // WM_HOTKEY fires on key down only (with MOD_NOREPEAT, no repeats).
            // We simulate push-to-talk: pressed on WM_HOTKEY, released when key-up
            // is detected via a separate low-level approach.
            // Simpler: toggle on each WM_HOTKEY (MOD_NOREPEAT makes this once per press).
            if (!_isPressed)
            {
                _isPressed = true;
                PushToTalkPressed?.Invoke();

                // Start listening for key-up via a background task
                StartKeyUpWatcher();
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// RegisterHotKey only fires on key-down. We poll for key-up using GetAsyncKeyState.
    /// This is lightweight (runs only while push-to-talk is active).
    /// </summary>
    private void StartKeyUpWatcher()
    {
        Task.Run(async () =>
        {
            // Extract primary key from virtual key code
            while (_isPressed)
            {
                await Task.Delay(16); // ~60fps polling
                // GetAsyncKeyState returns high bit set if key is down
                short state = GetAsyncKeyState((int)_virtualKey);
                bool keyDown = (state & 0x8000) != 0;
                if (!keyDown)
                {
                    _isPressed = false;
                    WpfApp.Current.Dispatcher.Invoke(() => PushToTalkReleased?.Invoke());
                    break;
                }
            }
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
            Win32.UnregisterHotKey(_hwnd, HotkeyId);
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }
}
