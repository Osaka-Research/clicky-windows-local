using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// Registers one or more global push-to-talk hotkeys using Win32 RegisterHotKey.
/// Non-intercepting: the key event still reaches all other applications.
/// Each registered hotkey fires its own Pressed/Released callbacks independently,
/// so e.g. Action mode (Ctrl+Shift+Space) and Answer mode (Ctrl+Shift+A) can coexist.
/// </summary>
public class HotkeyService : IDisposable
{
    private class Binding
    {
        public required int Id;
        public required uint Modifiers;
        public required uint VirtualKey;
        public required Action OnPressed;
        public required Action OnReleased;
        public bool IsPressed;
    }

    private const int FirstHotkeyId = 9001;

    private readonly List<Binding> _bindings = new();
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;

    /// <summary>
    /// Queues a hotkey to register on the next Register(window) call. modifiers/virtualKey
    /// use the same Win32 MOD_*/VK_* values as AppSettings' HotkeyModifiers/HotkeyVirtualKey.
    /// </summary>
    public void AddHotkey(uint modifiers, uint virtualKey, Action onPressed, Action onReleased)
    {
        _bindings.Add(new Binding
        {
            Id = FirstHotkeyId + _bindings.Count,
            Modifiers = modifiers | Win32.MOD_NOREPEAT, // suppress key-repeat while held
            VirtualKey = virtualKey,
            OnPressed = onPressed,
            OnReleased = onReleased,
        });
    }

    /// <summary>
    /// Must be called after the window's HWND is available (after SourceInitialized),
    /// and after all AddHotkey calls.
    /// </summary>
    public void Register(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.Handle;

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        foreach (var b in _bindings)
        {
            bool ok = Win32.RegisterHotKey(_hwnd, b.Id, b.Modifiers, b.VirtualKey);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"RegisterHotKey failed (error {err}). Try a different hotkey combination.");
            }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            var binding = _bindings.FirstOrDefault(b => b.Id == id);
            if (binding != null && !binding.IsPressed)
            {
                // WM_HOTKEY fires on key down only (with MOD_NOREPEAT, no repeats).
                // We simulate push-to-talk: pressed on WM_HOTKEY, released when key-up
                // is detected via polling GetAsyncKeyState.
                binding.IsPressed = true;
                binding.OnPressed();
                StartKeyUpWatcher(binding);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// RegisterHotKey only fires on key-down. We poll for key-up using GetAsyncKeyState.
    /// This is lightweight (runs only while push-to-talk is active).
    /// </summary>
    private void StartKeyUpWatcher(Binding binding)
    {
        Task.Run(async () =>
        {
            while (binding.IsPressed)
            {
                await Task.Delay(16); // ~60fps polling
                short state = GetAsyncKeyState((int)binding.VirtualKey);
                bool keyDown = (state & 0x8000) != 0;
                if (!keyDown)
                {
                    binding.IsPressed = false;
                    WpfApp.Current.Dispatcher.Invoke(binding.OnReleased);
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
        {
            foreach (var b in _bindings)
                Win32.UnregisterHotKey(_hwnd, b.Id);
        }
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }
}
