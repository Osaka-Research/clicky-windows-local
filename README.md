# Clicky Windows — local STT/TTS variant

A fork of [clicky_windows](https://github.com/emreyilmaz46/clicky_windows) (itself a
Windows port of [farzaa/clicky](https://github.com/farzaa/clicky)) that drops two of
the three cloud dependencies:

| | clicky_windows | this fork |
|---|---|---|
| Vision + reasoning + pointing | Claude (cloud) | Claude (cloud) — unavoidable, this is the one API doing the actual "seeing" |
| Speech-to-text | AssemblyAI (cloud WebSocket) | **local Whisper** (whisper.cpp via Whisper.net) — fully offline |
| Text-to-speech | ElevenLabs (cloud) | **Notepad** — writes the response to a text file and opens it |

Only one API key required now: **Anthropic**.

## Why "Notepad" for TTS

Literally what it sounds like — no real speech synthesis, no audio playback pipeline.
Claude's reply is written to `%TEMP%\ClickyWindowsLocal\response.txt` and opened in
`notepad.exe`. It fits the app's own house style — the original already opens Notepad
for its log viewer (`App.xaml.cs`'s "View Log" tray item). If a subsequent response
comes in while the last window is still open, that window is closed and reopened with
the new text (Notepad doesn't watch its file for external changes).

## Why local Whisper changes the pipeline shape

AssemblyAI's realtime WebSocket gave the original app turn-detection for free —
interim transcripts as you spoke, a final transcript event mid-hold. Local Whisper has
no equivalent; it transcribes a *finished* audio clip in one pass. So `CompanionManager`
here just buffers raw PCM16 while the hotkey is held and calls Whisper exactly once, on
release — push-to-talk already gives the start/end boundary a voice-activity detector
would otherwise need to guess at. Net effect: simpler code (no `TaskCompletionSource`
race, no 5s/8s finalize-retry dance), at the cost of not seeing a live transcript while
still talking.

## Setup

1. `dotnet build` (needs .NET 8 SDK on Windows — this is a WPF app, no Android-style SDK
   gymnastics, `dotnet build`/`dotnet run` just works).
2. Copy [`settings.example.json`](settings.example.json) to
   `%APPDATA%\ClickyWindowsLocal\settings.json`, fill in `AnthropicApiKey`.
3. Run it. First push-to-talk after a fresh install pauses a few seconds while the
   Whisper model downloads (~140MB for the default `base` size, cached after that).
4. Hold `Ctrl+Shift+Space` (default, same as upstream), speak, release.

## Whisper model size

`WhisperModelSize` in settings: `tiny` | `base` | `small` | `medium`. Bigger = more
accurate and multilingual-robust, slower per-clip, more RAM. `base` is the default and
a reasonable balance on CPU. `tiny` if this is running on modest hardware and you want
near-instant responses; `small`/`medium` if you have the CPU (or a GPU — see below) to
spare and want fewer misheard commands.

## GPU acceleration (optional)

Default build runs Whisper inference on CPU (`Whisper.net.Runtime`). If the machine has
an NVIDIA GPU, swap that package reference in the `.csproj` for
`Whisper.net.Runtime.Cuda` instead — same API, no code changes needed, meaningfully
faster transcription especially at `small`/`medium` model sizes.

## What's unchanged from clicky_windows

Everything else: the floating overlay (pointing dot, spinner, waveform bars), Claude's
system prompt and `[POINT:x,y:label]` protocol, the Computer Use two-phase precise
pointing, the hotkey service, screen capture, system tray. Only `AssemblyAIService` and
`ElevenLabsService` were swapped out, for `LocalWhisperService` and `NotepadTtsService`
respectively — `CompanionManager` is the one file with structural changes, everything
downstream of "Claude gave us a transcript" is identical to upstream.
