# Clicky Windows — local STT/TTS variant

A fork of [clicky_windows](https://github.com/emreyilmaz46/clicky_windows) (itself a
Windows port of [farzaa/clicky](https://github.com/farzaa/clicky)) that drops two of
the three cloud dependencies:

| | clicky_windows | this fork |
|---|---|---|
| Vision + reasoning + pointing | Claude (cloud) | Claude (cloud) — unavoidable, this is the one API doing the actual "seeing" |
| Speech-to-text | AssemblyAI (cloud WebSocket) | **local Whisper** (whisper.cpp via Whisper.net) — fully offline |
| Text-to-speech | ElevenLabs (cloud) | **SAPI** (`System.Speech.Synthesis`) — Windows' built-in speech engine, real spoken voice, fully offline |

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

## Why SAPI for TTS

`System.Speech.Synthesis` is built into Windows/.NET — no model download, no network
call, no extra native binaries, and it's a *real* spoken voice (not a text popup).
`SapiTtsService` picks a more natural-sounding installed voice (Zira/Aria/Jenny) over
the oldest defaults if one's available, otherwise falls back to whatever Windows'
Speech settings currently have selected. Same shape as `ElevenLabsService`
(`SpeakAsync`/`StopPlayback`/`PlaybackStarting`), so `CompanionManager` needed no other
changes to swap it in.

## Setup

1. `dotnet build` (needs .NET 8 SDK on Windows — this is a WPF app, no Android-style SDK
   gymnastics, `dotnet build`/`dotnet run` just works).
2. Run it. First launch (or whenever `AnthropicApiKey` is missing) opens a **Settings
   window** — enter your key, model, and optional base URL there, no JSON editing.
   Reachable any time after via the tray icon's **Settings...** item.
3. First push-to-talk after a fresh install pauses a few seconds while the Whisper
   model downloads (~140MB for the default `base` size, cached after that).
4. Hold `Ctrl+Shift+Space` (default, same as upstream), speak, release.

## Whisper model size

Set in the Settings window: `tiny` | `base` | `small` | `medium`. Bigger = more
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
`ElevenLabsService` were swapped out, for `LocalWhisperService` and `SapiTtsService`
respectively — `CompanionManager` is the one file with structural changes, everything
downstream of "Claude gave us a transcript" is identical to upstream.
