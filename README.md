# Clicky Windows — local STT/TTS variant

A fork of [clicky_windows](https://github.com/emreyilmaz46/clicky_windows) (itself a
Windows port of [farzaa/clicky](https://github.com/farzaa/clicky)) that drops two of
the three cloud dependencies:

| | clicky_windows | this fork |
|---|---|---|
| Vision + reasoning + pointing | Claude (cloud) | Claude (cloud) — unavoidable, this is the one API doing the actual "seeing" |
| Speech-to-text | AssemblyAI (cloud WebSocket) | **local Whisper** (whisper.cpp via Whisper.net) — fully offline |
| Text-to-speech | ElevenLabs (cloud) | **live reply panel** — a small always-on-top window, bottom-right corner, that streams Claude's answer as text in real time |

Only one API key required now: **Anthropic** (or any Anthropic-API-compatible
endpoint — the in-app Settings window has a base-URL field for that).

## Why local Whisper changes the pipeline shape

AssemblyAI's realtime WebSocket gave the original app turn-detection for free —
interim transcripts as you spoke, a final transcript event mid-hold. Local Whisper has
no equivalent; it transcribes a *finished* audio clip in one pass. So `CompanionManager`
here just buffers raw PCM16 while the hotkey is held and calls Whisper exactly once, on
release — push-to-talk already gives the start/end boundary a voice-activity detector
would otherwise need to guess at. Net effect: simpler code (no `TaskCompletionSource`
race, no 5s/8s finalize-retry dance), at the cost of not seeing a live transcript while
still talking.

## Why a live reply panel instead of TTS

`ReplyWindow` is a small always-on-top, borderless panel anchored to the top-right
corner (grows downward, capped height + scrollable past that) that shows both what you
said and Claude's answer streaming in as it's generated — no waiting for the full
response, no audio playback pipeline at all. The transcript line (italic, dim) appears
the instant Whisper finishes, before Claude has even started replying, so a
mis-transcription is obvious right away instead of only showing up as a confusing
answer. Text you read instead of a voice you wait through; closable any time via its
own `×` button.

`CompanionManager` streams the answer safely: it tracks the last unclosed `[` in the
raw response and holds back everything from there on, so a `[POINT:...]` tag never
flashes on screen even partially while it's still arriving — only revealed (stripped,
i.e. never shown at all) once it's either completed or turns out not to be a tag. Three
events drive the panel: `TranscriptReady` (show it with the "you said" line, as soon as
Whisper is done), `ReplyChunkReceived` (append answer text), `ReplyDismissed` (hide it —
fires if you press the hotkey again while a reply's still streaming in).

## Setup

1. `dotnet build` (needs .NET 8 SDK on Windows — this is a WPF app, no Android-style SDK
   gymnastics, `dotnet build`/`dotnet run` just works).
2. Run it. First launch (or whenever `AnthropicApiKey` is missing) opens a **Settings
   window** — enter your key, model, and optional base URL there, no JSON editing.
   Reachable any time after via the tray icon's **Settings...** item.
3. First push-to-talk after a fresh install pauses while the Whisper model downloads
   (~1.5GB for the default `medium.en` size — cached under %LOCALAPPDATA% after that,
   survives rebuilds/reinstalls).
4. Hold `Ctrl+Shift+1` for **Action mode**, `Ctrl+Shift+2` for **Answer mode**, or
   `Ctrl+Shift+3` for **System Audio mode** (all default, see below), speak (or, for
   System Audio, just let whatever's playing keep playing), release. Or just tap
   `Ctrl+Shift+4` for **Screenshot Q&A** — no hold needed, no mic involved at all.

## Four modes

Four independent hotkeys, all wired straight through `HotkeyService` (supports
registering any number of them) to the same `CompanionManager`, distinguished by
`InteractionMode` (`Models/AppState.cs`):

| | Action — `Ctrl+Shift+1` | Answer — `Ctrl+Shift+2` | System Audio — `Ctrl+Shift+3` | Screenshot Q&A — `Ctrl+Shift+4` |
|---|---|---|---|---|
| Trigger | Hold, speak, release | Hold, speak, release | Hold, release | **Tap** — fires on press, no hold |
| Audio source | Microphone | Microphone | **Speakers** (WASAPI loopback) | None |
| Screenshot | Captured and sent | Never captured | Never captured | Captured and sent |
| Claude can point at something on screen | Yes | No | No | Yes (but the point is to just answer) |
| Use for | "What's this error say", "click the X" | "What's the capital of France" | React to/explain a call, video, anything playing through speakers | Instantly answer every question visible right now |

System Audio mode uses `WasapiLoopbackCapture` instead of `WasapiCapture` in
`AudioCaptureService` — a subclass in NAudio, so the rest of the capture/conversion
pipeline needed no changes. The transcript gets wrapped with a bit of framing before
it's sent to Claude ("the following was just overheard... not spoken by the user
directly") so it reacts to/explains the content instead of treating it as a question
addressed to it directly.

Screenshot Q&A skips audio/Whisper entirely — `CompanionManager.OnScreenshotQaTriggered()`
fires straight off the hotkey's key-down event, captures one screenshot, and sends a
fixed prompt ("Answer every question visible on the screen, one by one.") alongside it.

The reply panel's header shows which mode produced the answer ("Clicky — Action" in
periwinkle, "Clicky — Answer" in green, "Clicky — System Audio" in amber, "Clicky —
Screen Q&A" in violet), so it's always clear afterward what was shared for that turn.
All four hotkeys are configurable in `settings.json`: `HotkeyModifiers`/
`HotkeyVirtualKey` (Action), `AnswerHotkeyModifiers`/`AnswerHotkeyVirtualKey` (Answer),
`SystemAudioHotkeyModifiers`/`SystemAudioHotkeyVirtualKey` (System Audio), and
`ScreenshotQaHotkeyModifiers`/`ScreenshotQaHotkeyVirtualKey` (Screenshot Q&A).

## Whisper model size

Set in the Settings window: `tiny` / `tiny.en` | `base` / `base.en` | `small` /
`small.en` | `medium` / `medium.en`. The `.en` variants are English-only and more
accurate than their multilingual counterpart at the same size (smaller vocabulary to
disambiguate) — `medium.en` is the default, since GPU acceleration (see below) makes
the accuracy jump essentially free. Drop the `.en` suffix if you need other languages
recognized; `LocalWhisperService` switches Whisper's language mode between fixed
`"en"` and `"auto"` based on whichever you pick, no other setting needed. Drop to
`small.en` or `base.en` if running CPU-only and the latency bothers you.

Bigger size = more accurate, slower per-clip, more RAM. `tiny`/`tiny.en` if this is
running on modest hardware and you want near-instant responses; `small`/`medium` (or
their `.en` variants) if you have the CPU (or a GPU — see below) to
spare and want fewer misheard commands.

## GPU acceleration

Both `Whisper.net.Runtime` (CPU) and `Whisper.net.Runtime.Cuda` are referenced in the
`.csproj` together — Whisper.net auto-detects and prefers CUDA when a working GPU
setup is present, falling back to CPU otherwise. No code change either way.

**The CUDA runtime DLL is pinned to a specific CUDA major version** — check with
`Select-String -Path runtimes\cuda\win-x64\ggml-cuda-whisper.dll -Pattern 'cudart64_\d+' -Encoding Ascii`
to see which one your build actually needs (currently `cudart64_12`, i.e. **CUDA
Toolkit 12.x** — not 13.x, even though 13.x is winget's/NVIDIA's current default).
Windows DLL loading is exact-name, no cross-major fallback: if the installed Toolkit
is the wrong major version, `ggml-cuda-whisper.dll` fails to load its dependency and
the *whole app crashes* on the `WhisperFactory.FromPath()` call — silently, no managed
exception, nothing in `clicky.log` — rather than gracefully falling back to CPU. Multiple
CUDA Toolkit major versions can be installed side by side with no conflict (they live in
separate `CUDA\v12.x` / `CUDA\v13.x` folders under `C:\Program Files\NVIDIA GPU Computing
Toolkit`), so if you already have a newer one for something else, just add 12.x alongside
it rather than replacing anything.

Also needs a driver new enough for CUDA 12.x (check with `nvidia-smi`, look at the `CUDA
Version` it reports) — a several-year-old driver may only support CUDA 10.x/11.x and
need updating first.

## What's unchanged from clicky_windows

Everything else: the floating overlay (pointing dot, spinner, waveform bars), Claude's
system prompt and `[POINT:x,y:label]` protocol, the Computer Use two-phase precise
pointing, the hotkey service, screen capture, system tray. `AssemblyAIService` and
`ElevenLabsService` were swapped out for `LocalWhisperService` and `ReplyWindow` +
streaming logic in `CompanionManager` respectively.
