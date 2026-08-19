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
4. Hold `Ctrl+Shift+Space` for **Action mode**, or `Ctrl+Shift+A` for **Answer mode**
   (both default, see below), speak, release.

## Action mode vs. Answer mode

Two independent push-to-talk hotkeys, both wired straight through `HotkeyService` (now
supports registering more than one) to the same `CompanionManager`:

| | Action mode — `Ctrl+Shift+Space` | Answer mode — `Ctrl+Shift+A` |
|---|---|---|
| Screenshot | Captured and sent with the question | **Never captured, never sent** |
| Claude can point at something on screen | Yes | No — nothing to point at |
| Use for | "What's this error say", "click the X" | "What's the capital of France", anything that doesn't need your screen |

The reply panel's header shows which mode produced the answer ("Clicky — Action" in
periwinkle, "Clicky — Answer" in green), so it's always clear afterward whether the
screen was shared for that turn. Both hotkeys are configurable via `HotkeyModifiers`/
`HotkeyVirtualKey` (Action) and `AnswerHotkeyModifiers`/`AnswerHotkeyVirtualKey`
(Answer) in `settings.json`.

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
