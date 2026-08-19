using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

/// <summary>
/// Orchestrates the full voice interaction loop:
///   hotkey press → buffer mic audio → local Whisper transcription → Claude → overlay + live reply panel
///
/// Differs from the cloud original in one structural way: AssemblyAI's realtime WebSocket
/// gave us turn-detection (interim/final transcripts as you spoke) for free. Local Whisper
/// has no such thing — it transcribes a finished clip in one shot. So instead of awaiting a
/// TaskCompletionSource resolved by a "final transcript" event, this buffers raw PCM16 while
/// the hotkey is held and only calls Whisper once, on release. Simpler, and push-to-talk
/// already gives us the start/end boundary a VAD would otherwise be needed for.
/// </summary>
public class CompanionManager : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly AudioCaptureService _audio;
    private readonly ScreenCaptureService _screen;
    private readonly ClaudeService _claude;
    private readonly LocalWhisperService _whisper;

    private CancellationTokenSource? _sessionCts;
    private readonly List<byte> _audioBuffer = new();
    private InteractionMode _modeThisTurn = InteractionMode.Action;

    public event Action<AppState>? StateChanged;
    public event Action<double, double, string>? PointReceived;
    public event Action<float>? AudioLevelChanged;
    /// <summary>Fires with a short message when a pipeline stage fails silently.</summary>
    public event Action<string>? FeedbackReceived;
    /// <summary>Fires once the mic has stopped and transcription is about to start — triggers the spinner pulse.</summary>
    public event Action? TranscriptConfirmed;
    /// <summary>Fires as soon as Whisper produces a transcript — UI should show the reply panel with
    /// this text, before Claude has even started replying.</summary>
    public event Action<string, InteractionMode>? TranscriptReady;
    /// <summary>Fires with each new piece of reply text as it's safe to reveal (POINT tags never shown, even partially).</summary>
    public event Action<string>? ReplyChunkReceived;
    /// <summary>Fires when a reply in progress is interrupted by a new push-to-talk press — UI should hide the panel.</summary>
    public event Action? ReplyDismissed;

    private AppState _state = AppState.Idle;
    private AppState State
    {
        get => _state;
        set
        {
            _state = value;
            Logger.Log($"[State] → {value}");
            StateChanged?.Invoke(value);
        }
    }

    public CompanionManager(AppSettings settings)
    {
        _settings = settings;
        _audio = new AudioCaptureService();
        _screen = new ScreenCaptureService();
        _claude = new ClaudeService(settings);
        _whisper = new LocalWhisperService(settings.WhisperModelSize);

        _audio.PowerLevelChanged += level => AudioLevelChanged?.Invoke(level);

        // Kick off the (potentially slow, first-run-only) model download/load in the
        // background at startup so the first push-to-talk isn't the one paying for it.
        _ = _whisper.EnsureModelLoadedAsync();
    }

    // ── Push-to-talk lifecycle ──────────────────────────────────────────────

    /// <summary>
    /// Allows starting a new session while a reply is still streaming in (dismisses it),
    /// but refuses if a session is already actively Listening/Processing. Returns whether
    /// the caller may proceed.
    /// </summary>
    private bool TryBeginNewSession(string logContext)
    {
        if (State == AppState.Speaking)
        {
            Logger.Log("[Hotkey] Interrupting — dismissing in-progress reply");
            _sessionCts?.Cancel();
            ReplyDismissed?.Invoke();
            _state = AppState.Idle; // set directly to avoid double-firing state events
        }

        if (State != AppState.Idle)
        {
            Logger.Log($"[Hotkey] {logContext} but state={State} — ignoring");
            return false;
        }
        return true;
    }

    public Task OnPushToTalkPressed(InteractionMode mode = InteractionMode.Action)
    {
        if (!TryBeginNewSession("Push-to-talk pressed")) return Task.CompletedTask;

        Logger.Log($"[Hotkey] Push-to-talk PRESSED (mode={mode})");
        _modeThisTurn = mode;
        State = AppState.Listening;
        _sessionCts = new CancellationTokenSource();

        lock (_audioBuffer) _audioBuffer.Clear();
        _audio.AudioChunkAvailable += OnAudioChunk;
        _audio.Start(loopback: mode == InteractionMode.SystemAudio);
        Logger.Log("[Audio] WASAPI capture started");

        return Task.CompletedTask;
    }

    // Shown in the reply panel's "you said" line -- short, since the real instruction below is not.
    private const string ScreenshotQaDisplayLabel = "Answer everything relevant on screen";

    private const string ScreenshotQaPrompt = """
        First figure out what's actually on screen and what the user is likely doing with it --
        a video call with an interviewer (question may be spoken and only partly visible as
        a caption, or written in a chat panel), a coding platform or take-home problem, a
        webpage, a PDF, a job description or resume, etc. Then focus on the content that
        context implies, not just whatever text happens to be biggest: on a call, it's the
        chat/question panel or shared doc, not the participant tiles or call controls; on a
        webpage, it's the page content, not the browser chrome/tabs/bookmarks bar. If there's
        more than one question or part visible, answer each one in turn, but as one
        continuous spoken answer -- not a labeled list.
        """;

    /// <summary>
    /// Ctrl+Shift+Q: fires immediately on key press, no hold/release and no mic involved
    /// at all -- captures a single screenshot and asks Claude to answer everything
    /// visible in it.
    /// </summary>
    public async Task OnScreenshotQaTriggered()
    {
        if (!TryBeginNewSession("Screenshot Q&A pressed")) return;

        Logger.Log("[Hotkey] Screenshot Q&A PRESSED");
        _modeThisTurn = InteractionMode.ScreenshotQA;
        State = AppState.Processing;
        _sessionCts = new CancellationTokenSource();

        List<ScreenshotResult> screenshots;
        try
        {
            screenshots = _screen.CaptureAll();
            Logger.Log($"[Screen] Captured {screenshots.Count} display(s)");
        }
        catch (Exception ex)
        {
            Logger.Log($"[Screen] Capture failed: {ex.Message}");
            screenshots = [];
        }

        TranscriptConfirmed?.Invoke();
        TranscriptReady?.Invoke(ScreenshotQaDisplayLabel, _modeThisTurn);
        await ProcessResponseAsync(ScreenshotQaPrompt, screenshots);
    }

    public async Task OnPushToTalkReleased()
    {
        Logger.Log($"[Hotkey] Push-to-talk RELEASED (state={State})");

        if (State != AppState.Listening)
        {
            Logger.Log("[Hotkey] State is not Listening — an error occurred earlier, see log");
            return;
        }

        State = AppState.Processing;

        _audio.AudioChunkAvailable -= OnAudioChunk;
        await StopAudioWithTimeoutAsync();

        byte[] clip;
        lock (_audioBuffer) clip = _audioBuffer.ToArray();

        // Capture screens while Whisper is (about to be) working — same as the cloud build.
        // Skipped entirely outside Action mode: nothing is captured, nothing is sent.
        List<ScreenshotResult> screenshots = [];
        if (_modeThisTurn == InteractionMode.Action)
        {
            try
            {
                screenshots = _screen.CaptureAll();
                Logger.Log($"[Screen] Captured {screenshots.Count} display(s)");
            }
            catch (Exception ex)
            {
                Logger.Log($"[Screen] Capture failed: {ex.Message}");
            }
        }
        else
        {
            Logger.Log($"[Screen] {_modeThisTurn} mode — no screenshot captured");
        }

        TranscriptConfirmed?.Invoke();

        string transcript = "";
        try
        {
            Logger.Log($"[Whisper] Transcribing {clip.Length} bytes ({clip.Length / 32000.0:F1}s)...");
            transcript = await _whisper.TranscribeAsync(clip, _sessionCts!.Token);
            Logger.Info($"Heard: \"{transcript}\"");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"[Whisper] Transcription error: {ex.Message}");
            FeedbackReceived?.Invoke("couldn't transcribe that");
        }

        if (!string.IsNullOrWhiteSpace(transcript))
        {
            TranscriptReady?.Invoke(transcript, _modeThisTurn);

            // System Audio mode: the transcript is overheard content (a call, a video),
            // not something the user said to Clicky directly -- frame it as such so Claude
            // reacts to/explains it rather than treating it as a question addressed to it.
            var messageForClaude = _modeThisTurn == InteractionMode.SystemAudio
                ? $"[The following was just overheard playing on the computer's speakers -- not spoken by the user directly]: \"{transcript}\""
                : transcript;

            await ProcessResponseAsync(messageForClaude, screenshots);
        }
        else
        {
            Logger.Log("[Pipeline] No transcript — returning to Idle");
            if (!_sessionCts!.IsCancellationRequested)
                FeedbackReceived?.Invoke("didn't catch that");
            State = AppState.Idle;
        }
    }

    private void OnAudioChunk(byte[] pcm16)
    {
        lock (_audioBuffer) _audioBuffer.AddRange(pcm16);
    }

    // NAudio's WasapiCapture.StopRecording()/Dispose() are plain blocking calls with no
    // cancellation support -- if the native WASAPI stop call itself ever wedges (driver
    // quirk, device disconnect mid-capture), there's no way to un-stick it. Racing it
    // against a timeout at least keeps the app responsive instead of hanging the whole
    // turn (and every turn after it) forever; the abandoned capture object leaks, but
    // the next Start() creates a fresh one regardless, so the app self-recovers.
    private static readonly TimeSpan AudioStopTimeout = TimeSpan.FromSeconds(5);

    private async Task StopAudioWithTimeoutAsync()
    {
        var stopTask = Task.Run(() => _audio.Stop());
        var winner = await Task.WhenAny(stopTask, Task.Delay(AudioStopTimeout));
        if (winner == stopTask)
        {
            Logger.Log("[Audio] Capture stopped");
        }
        else
        {
            Logger.Error($"[Audio] Stop() didn't return within {AudioStopTimeout.TotalSeconds:F0}s " +
                         "(likely a stuck native WASAPI call) — abandoning it and continuing. " +
                         "Next capture will use a fresh device handle.");
        }
    }

    // ── Claude + live reply panel ───────────────────────────────────────────

    private async Task ProcessResponseAsync(string transcript, List<ScreenshotResult> screenshots)
    {
        Logger.Log($"[Claude] Sending to Claude: \"{transcript}\" with {screenshots.Count} screenshot(s)");

        var responseBuilder = new System.Text.StringBuilder();
        PointTarget? detectedPoint = null;
        int revealedLength = 0;
        bool started = false;

        try
        {
            await foreach (var chunk in _claude.StreamResponseAsync(transcript, screenshots, _modeThisTurn, _sessionCts!.Token))
            {
                responseBuilder.Append(chunk);

                if (detectedPoint == null)
                {
                    var pts = ClaudeService.ParsePoints(responseBuilder.ToString());
                    if (pts.Count > 0)
                        detectedPoint = pts[0];
                }

                if (!started)
                {
                    started = true;
                    State = AppState.Speaking;
                }

                // Only reveal text we're sure isn't the start of an in-progress [POINT:...] tag —
                // find the last '[' in the raw stream and hold back everything from there on
                // until it's either closed (tag complete, gets stripped normally) or turns out
                // not to be a tag at all. Keeps partial tags from ever flashing on screen.
                var raw = responseBuilder.ToString();
                var lastOpen = raw.LastIndexOf('[');
                var safeRaw = (lastOpen == -1 || raw.IndexOf(']', lastOpen) != -1) ? raw : raw[..lastOpen];
                var safeText = ClaudeService.StripPointTags(safeRaw);

                if (safeText.Length > revealedLength)
                {
                    ReplyChunkReceived?.Invoke(safeText[revealedLength..]);
                    revealedLength = safeText.Length;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Claude API error: {ex.Message}");
            FeedbackReceived?.Invoke("couldn't get a response");
            State = AppState.Idle;
            return;
        }

        // Reveal whatever's left now that the stream is finished (final tags, if any, are complete).
        var fullText = ClaudeService.StripPointTags(responseBuilder.ToString());
        if (fullText.Length > revealedLength)
            ReplyChunkReceived?.Invoke(fullText[revealedLength..]);
        Logger.Log($"[Claude] Response text: \"{fullText}\"");

        if (detectedPoint != null && screenshots.Count > 0)
        {
            Logger.Log($"[Claude] POINT detected: label=\"{detectedPoint.Label}\" — starting CU coordinate refinement");
            bool cuSucceeded = false;

            try
            {
                var primaryShot = screenshots[0];
                var (cuW, cuH) = CoordinateHelper.DetectComputerUseResolution(
                    primaryShot.Bounds.Width, primaryShot.Bounds.Height);

                var resized = _screen.CaptureResized(primaryShot.Bounds, cuW, cuH);
                if (resized != null)
                {
                    var (physX, physY) = await _claude.DetectElementAsync(
                        resized.Base64, detectedPoint.Label,
                        primaryShot.Bounds.Width, primaryShot.Bounds.Height,
                        _sessionCts!.Token);

                    if (physX >= 0)
                    {
                        Logger.Log($"[CU] Precise POINT: ({physX},{physY}) label=\"{detectedPoint.Label}\"");
                        WpfApp.Current.Dispatcher.Invoke(() =>
                            PointReceived?.Invoke(physX, physY, detectedPoint.Label));
                        cuSucceeded = true;
                    }
                    else
                    {
                        Logger.Log("[CU] No tool_use coordinate in response");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[CU] Computer Use detection failed: {ex.Message}");
            }

            if (!cuSucceeded)
            {
                Logger.Log($"[CU] Falling back to rough coords ({detectedPoint.X},{detectedPoint.Y})");
                WpfApp.Current.Dispatcher.Invoke(() =>
                    PointReceived?.Invoke(detectedPoint.X, detectedPoint.Y, detectedPoint.Label));
            }
        }

        State = AppState.Idle;
    }

    public async ValueTask DisposeAsync()
    {
        _sessionCts?.Cancel();
        _audio.AudioChunkAvailable -= OnAudioChunk;
        _audio.Dispose();
        await _whisper.DisposeAsync();
        _sessionCts?.Dispose();
    }
}
