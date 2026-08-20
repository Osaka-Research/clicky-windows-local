# Auto (Mac)

Native macOS port, sharing all the actual logic (Claude API calls, per-mode conversation
history, the interview-script prompts, local Whisper transcription) with the Windows build
via `Clicky.Core` — see that project's comments for how the shared pieces work. This
project is only the macOS-specific glue: hotkeys, audio/screen capture, and the tray icon
+ reply panel UI.

**Built without access to a Mac.** Everything here compiles against documented Apple APIs
and well-established patterns, but none of it has actually run. Treat first launch as a
debugging session, not a demo — see "What to check first" below.

## Prerequisites

- .NET 8 SDK
- [`sox`](https://formulae.brew.sh/formula/sox) for audio capture: `brew install sox`
- For **System Audio mode** specifically: a virtual audio device, since macOS has no
  native loopback API the way Windows has WASAPI loopback. [BlackHole](https://github.com/ExistentialAudio/BlackHole)
  is the standard free option: `brew install blackhole-2ch`, then in **System Settings →
  Sound**, set your output to a Multi-Output Device that includes both BlackHole and your
  real speakers/headphones (so you still hear things), and set
  `SystemAudioInputDeviceName` in Settings to `"BlackHole 2ch"`. Without this configured,
  System Audio mode falls back to the microphone rather than failing outright.
- **Screen Recording** permission (System Settings → Privacy & Security → Screen
  Recording) — macOS will prompt for this automatically the first time a hotkey that
  captures a screenshot fires; the app won't appear in the permission list until then.
- **Microphone** permission, prompted automatically on first mic use.
- Global hotkeys use the Carbon Event Manager's `RegisterEventHotKey`, which — unlike
  `CGEventTap`-based approaches — does **not** need Accessibility/Input Monitoring
  permission. One less permission dialog to worry about, and one less way this build
  could look broken because of an unrelated OS permission gate.

## Building

```
cd Clicky.Mac
dotnet build
dotnet run
```

No Xcode project, no code signing needed to run locally — this is a plain .NET console-style
app using Avalonia for UI, same `dotnet build`/`dotnet run` workflow as the Windows build.
(Code signing and notarization only start to matter if this is ever distributed outside
your own machine.)

## What to check first if something doesn't work

Roughly in order of how likely each is to be the actual problem, worst first:

1. **Hotkeys don't fire at all.** `Native/Carbon.cs` and `Services/MacHotkeyService.cs`
   are the riskiest untested piece — the P/Invoke signatures are correct per Apple's
   documented headers, but the FourCC constants, struct layout, or `EventHandlerProc`
   marshaling could still be subtly wrong. Check `clicky.log` for
   `RegisterEventHotKey failed` — if it's not even getting that far (no log line about
   hotkeys at all), the crash is likely happening inside `InstallEventHandler` or
   `NewEventHandlerUPP` before any hotkey registration is attempted.
2. **The reply panel is still visible in a screen share.** `Native/WindowCapture.cs`'s
   `objc_msgSend` call for `setSharingType:` may need the window's native handle fetched
   at a different point in the lifecycle than `Opened` — try moving the
   `WindowCapture.ExcludeFromCapture(this)` call to right after the *first* `Show()`
   instead if `Opened` fires too early.
3. **No sound is captured.** Check `sox` is actually resolvable — `ResolveSoxPath()` in
   `MacAudioCaptureService.cs` tries common Homebrew paths and then `which sox` via a
   login shell; run `clicky.log` past a push-to-talk press to see which path (if any) it
   picked, or whether it logged "sox not found".
4. **Screenshots come back empty/black.** Almost always Screen Recording permission not
   yet granted — macOS won't prompt until the exact moment `screencapture` first tries to
   run, and the resulting file can be zero-byte or a black rectangle rather than an
   obvious error.
5. **Tray icon looks wrong/blank.** No custom icon bitmap is set yet (see
   `SetupTrayIcon()` in `App.axaml.cs`) — Avalonia's fallback appearance hasn't been
   checked since there's nowhere to check it. Cosmetic only; drop a bitmap in and wire up
   `TrayIcon.Icon` once this can actually run.

## Known gaps vs. the Windows build

- **No pointing-dot overlay.** Action mode's `[POINT:...]` tags are still parsed and the
  Computer Use coordinate-refinement call still runs, but nothing draws the animated dot
  on screen the way `OverlayWindow` does on Windows — `CompanionManager.PointReceived`
  just has no subscriber on Mac yet. Given the interview-prep use case is centered on the
  reply panel/script rather than pointing at UI elements, this seemed like the right
  scope cut given zero ability to test animation code blind; add an Avalonia
  `OverlayWindow` port later if it's actually wanted.
- **Single combined screenshot**, not one per display like Windows' `CaptureAll()` — see
  the comment in `MacScreenCaptureService.cs`.
- **System Audio mode needs a virtual device configured**, unlike Windows' native WASAPI
  loopback — see Prerequisites above.
