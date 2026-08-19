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

`ReplyWindow` is a small always-on-top, borderless panel anchored to the bottom-right
corner that streams Claude's answer in as it's generated — no waiting for the full
response, no audio playback pipeline at all. Text you read instead of a voice you wait
through; closable any time via its own `×` button.

`CompanionManager` streams safely: it tracks the last unclosed `[` in the raw response
and holds back everything from there on, so a `[POINT:...]` tag never flashes on
screen even partially while it's still arriving — only revealed (stripped, i.e. never
shown at all) once it's either completed or turns out not to be a tag. Three new events
drive it: `ReplyStarted` (clear + show the panel), `ReplyChunkReceived` (append text),
`ReplyDismissed` (hide it — fires if you press the hotkey again while a reply's still
streaming in).

## Setup

1. `dotnet build` (needs .NET 8 SDK on Windows — this is a WPF app, no Android-style SDK
   gymnastics, `dotnet build`/`dotnet run` just works).
2. Run it. First launch (or whenever `AnthropicApiKey` is missing) opens a **Settings
   window** — enter your key, model, and optional base URL there, no JSON editing.
   Reachable any time after via the tray icon's **Settings...** item.
3. First push-to-talk after a fresh install pauses a few seconds while the Whisper
   model downloads (~140MB for the default `base.en` size, cached after that).
4. Hold `Ctrl+Shift+Space` (default, same as upstream), speak, release.

## Whisper model size

Set in the Settings window: `tiny` / `tiny.en` | `base` / `base.en` | `small` /
`small.en` | `medium` / `medium.en`. The `.en` variants are English-only and more
accurate than their multilingual counterpart at the same size (smaller vocabulary to
disambiguate) — `base.en` is the default. Drop the `.en` suffix if you need other
languages recognized; `LocalWhisperService` switches Whisper's language mode between
fixed `"en"` and `"auto"` based on whichever you pick, no other setting needed.

Bigger size = more accurate, slower per-clip, more RAM. `tiny`/`tiny.en` if this is
running on modest hardware and you want near-instant responses; `small`/`medium` (or
their `.en` variants) if you have the CPU (or a GPU — see below) to
spare and want fewer misheard commands.

## GPU acceleration (optional)

Default build runs Whisper inference on CPU (`Whisper.net.Runtime`). If the machine has
an NVIDIA GPU, swap that package reference in the `.csproj` for
`Whisper.net.Runtime.Cuda` instead — same API, no code changes needed, meaningfully
faster transcription especially at `small`/`medium` model sizes.

## What's unchanged from clicky_windows

Everything else: the floating overlay (pointing dot, spinner, waveform bars), Claude's
system prompt and `[POINT:x,y:label]` protocol, the Computer Use two-phase precise
pointing, the hotkey service, screen capture, system tray. `AssemblyAIService` and
`ElevenLabsService` were swapped out for `LocalWhisperService` and `ReplyWindow` +
streaming logic in `CompanionManager` respectively.
