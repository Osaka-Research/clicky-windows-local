using ClickyMac.Native;
using ClickyWindows.Helpers;

namespace ClickyMac.Services;

public record FailedHotkey(uint Modifiers, uint VirtualKey, int OsStatus);

/// <summary>
/// Global hotkeys via Carbon's RegisterEventHotKey (see Native/Carbon.cs). Mirrors the
/// Windows HotkeyService's shape (AddHotkey/Register/Dispose/FailedHotkeys) so App.axaml.cs
/// wires up identically to MainWindow.xaml.cs. Never throws on a conflicting hotkey --
/// that exact behavior (single failed RegisterHotKey crashing the whole app before it
/// even showed a window) was a real, hard-to-diagnose bug on the Windows build; failures
/// here are collected into FailedHotkeys instead so the caller can surface them and move on.
/// </summary>
public class MacHotkeyService : IDisposable
{
    private class Binding
    {
        public required uint Modifiers;
        public required uint VirtualKey;
        public required uint Id;
        public required Action OnPressed;
        public required Action OnReleased;
        public IntPtr HotKeyRef;
        public bool Registered;
    }

    private readonly List<Binding> _bindings = new();
    private readonly Dictionary<uint, Binding> _byId = new();
    private uint _nextId = 1;
    private IntPtr _eventHandlerRef;
    private Carbon.EventHandlerProc? _handlerDelegate; // field, not local -- must outlive Register()

    public IReadOnlyList<FailedHotkey> FailedHotkeys { get; private set; } = [];

    public void AddHotkey(uint modifiers, uint virtualKey, Action onPressed, Action onReleased)
    {
        _bindings.Add(new Binding
        {
            Modifiers = modifiers,
            VirtualKey = virtualKey,
            Id = _nextId++,
            OnPressed = onPressed,
            OnReleased = onReleased,
        });
    }

    public void Register()
    {
        _handlerDelegate = HandleEvent;
        var handlerUpp = Carbon.NewEventHandlerUPP(_handlerDelegate);

        var specs = new[]
        {
            new Carbon.EventTypeSpec { eventClass = Carbon.kEventClassKeyboard, eventKind = Carbon.kEventHotKeyPressed },
            new Carbon.EventTypeSpec { eventClass = Carbon.kEventClassKeyboard, eventKind = Carbon.kEventHotKeyReleased },
        };
        Carbon.InstallEventHandler(
            Carbon.GetApplicationEventTarget(), handlerUpp, specs.Length, specs, IntPtr.Zero, out _eventHandlerRef);

        var failed = new List<FailedHotkey>();
        foreach (var b in _bindings)
        {
            var hotKeyId = new Carbon.EventHotKeyID { signature = Carbon.ClickySignature, id = b.Id };
            int status = Carbon.RegisterEventHotKey(
                b.VirtualKey, b.Modifiers, hotKeyId, Carbon.GetApplicationEventTarget(), 0, out var hotKeyRef);

            if (status == 0)
            {
                b.HotKeyRef = hotKeyRef;
                b.Registered = true;
                _byId[b.Id] = b;
            }
            else
            {
                Logger.Error($"[Hotkey] RegisterEventHotKey failed for modifiers=0x{b.Modifiers:X} " +
                             $"keycode=0x{b.VirtualKey:X} (OSStatus {status}) -- probably claimed by " +
                             "another app. That mode won't respond this run.");
                failed.Add(new FailedHotkey(b.Modifiers, b.VirtualKey, status));
            }
        }
        FailedHotkeys = failed;
    }

    private int HandleEvent(IntPtr inHandlerCallRef, IntPtr inEvent, IntPtr inUserData)
    {
        uint kind = Carbon.GetEventKind(inEvent);
        int status = Carbon.GetEventParameter(
            inEvent, Carbon.kEventParamDirectObject, Carbon.typeEventHotKeyID,
            out _, System.Runtime.InteropServices.Marshal.SizeOf<Carbon.EventHotKeyID>(),
            out _, out var hotKeyId);

        if (status != 0 || !_byId.TryGetValue(hotKeyId.id, out var binding))
            return -9874; // eventNotHandledErr

        try
        {
            if (kind == Carbon.kEventHotKeyPressed) binding.OnPressed();
            else if (kind == Carbon.kEventHotKeyReleased) binding.OnReleased();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Hotkey] handler threw: {ex.Message}");
        }

        return 0; // noErr
    }

    public void Dispose()
    {
        foreach (var b in _bindings.Where(b => b.Registered))
            Carbon.UnregisterEventHotKey(b.HotKeyRef);
        if (_eventHandlerRef != IntPtr.Zero)
            Carbon.RemoveEventHandler(_eventHandlerRef);
    }
}
