using System.Threading.Channels;
using Auto.Helpers;
using Auto.Models;
using Auto.Settings;

namespace Auto.Services;

/// <summary>
/// Orchestrates the full voice interaction loop:
///   hotkey press → buffer mic audio → local Whisper transcription → Claude → overlay + live reply panel
///
/// Platform-agnostic: talks to audio/screen capture only through IAudioCaptureService /
/// IScreenCaptureService, and marshals UI-affecting callbacks through the injected
/// [uiDispatch] delegate (WPF's Dispatcher.Invoke on Windows, Avalonia's Dispatcher.UIThread
/// on macOS) rather than any platform-specific UI type.
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
    private readonly IAudioCaptureService _audio;
    private readonly IScreenCaptureService _screen;
    private readonly IInferenceBackend _inference;
    private readonly Action<Action> _uiDispatch;

    private CancellationTokenSource? _sessionCts;
    private readonly List<byte> _audioBuffer = new();
    private InteractionMode _modeThisTurn = InteractionMode.Action;

    // ── System Audio continuous mode (toggle on/off instead of hold/release) ──
    // No hold key to mark turn boundaries here, so silence in the RMS level (from the
    // same loopback stream, already computed for the level meter) marks the end of a
    // turn instead: once level drops below threshold for ContinuousSilenceCut after
    // having been above it, whatever's buffered since the last cut is handed off as one
    // turn. Turns are queued and answered one at a time while capture keeps running.
    //
    // There's no fixed level that works everywhere -- a quiet room and a noisy one need
    // different cutoffs, and this pipeline has no noise suppression of its own (raw WASAPI
    // loopback straight to PCM16). So the threshold is calibrated per session: the first
    // ContinuousCalibrationWindow of capture is treated as ambient noise, not speech, and
    // the lowest RMS seen in that window becomes the noise floor the cutoff sits above.
    private bool _continuousAudioActive;
    private Channel<byte[]>? _continuousTurnChannel;
    private readonly List<byte> _continuousSegment = new();
    private bool _continuousHasSpeech;
    private DateTime _continuousLastLoud = DateTime.MinValue;

    private bool _continuousCalibrating;
    private DateTime _continuousCalibrationStart;
    private float _continuousCalibrationMin;
    private float _continuousVadThreshold;

    private static readonly TimeSpan ContinuousCalibrationWindow = TimeSpan.FromMilliseconds(600);
    private const float ContinuousVadMargin = 0.02f;
    private const float ContinuousVadMinThreshold = 0.015f;
    private const float ContinuousVadMaxThreshold = 0.2f;
    private static readonly TimeSpan ContinuousSilenceCut = TimeSpan.FromMilliseconds(900);
    private const int ContinuousMinSegmentBytes = 16000; // ~0.5s of 16kHz mono PCM16

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
    /// <summary>Fires when a fresh System Audio continuous session starts (toggle pressed while off) —
    /// UI should clear any chat log left over from a previous session.</summary>
    public event Action? ContinuousSessionStarted;

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

    /// <param name="uiDispatch">
    /// Runs an action on the UI thread -- e.g. `a => Dispatcher.UIThread.Invoke(a)` on
    /// Avalonia, `a => Application.Current.Dispatcher.Invoke(a)` on WPF.
    /// </param>
    public CompanionManager(
        AppSettings settings,
        IAudioCaptureService audio,
        IScreenCaptureService screen,
        IInferenceBackend inference,
        Action<Action> uiDispatch)
    {
        _settings = settings;
        _audio = audio;
        _screen = screen;
        _inference = inference;
        _uiDispatch = uiDispatch;

        _audio.PowerLevelChanged += level => AudioLevelChanged?.Invoke(level);
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

    public async Task OnPushToTalkPressed(InteractionMode mode = InteractionMode.Action)
    {
        if (_continuousAudioActive) await StopContinuousSystemAudioAsync();

        if (!TryBeginNewSession("Push-to-talk pressed")) return;

        Logger.Log($"[Hotkey] Push-to-talk PRESSED (mode={mode})");
        _modeThisTurn = mode;
        State = AppState.Listening;
        _sessionCts = new CancellationTokenSource();

        lock (_audioBuffer) _audioBuffer.Clear();
        _audio.AudioChunkAvailable += OnAudioChunk;
        _audio.Start(loopback: mode == InteractionMode.SystemAudio);
        Logger.Log("[Audio] capture started");
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
    /// Screenshot Q&amp;A hotkey: fires immediately on key press, no hold/release and no mic
    /// involved at all -- captures a single screenshot and asks Claude to answer everything
    /// visible in it.
    /// </summary>
    public async Task OnScreenshotQaTriggered()
    {
        if (_continuousAudioActive) await StopContinuousSystemAudioAsync();

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
            transcript = await _inference.TranscribeAsync(clip, _sessionCts!.Token);
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

            // System Audio and Answer modes: keep the answer short and plain, no framing.
            var messageForClaude = _modeThisTurn is InteractionMode.SystemAudio or InteractionMode.Answer
                ? $"Answer in under 100 words, like a human talking, using simple everyday " +
                  $"language, no jargon: \"{transcript}\""
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

    // ── System Audio continuous mode ────────────────────────────────────────

    /// <summary>
    /// Ctrl+Shift+3 as a toggle instead of hold/release: first press starts continuous
    /// listening (turns are cut automatically on silence, answered one at a time, capture
    /// keeps running), second press on the same hotkey stops it.
    /// </summary>
    public async Task OnSystemAudioTogglePressed()
    {
        if (_continuousAudioActive)
        {
            await StopContinuousSystemAudioAsync();
            return;
        }

        if (!TryBeginNewSession("System Audio toggle pressed")) return;

        Logger.Log("[Hotkey] System Audio continuous mode ON");
        _continuousAudioActive = true;
        _modeThisTurn = InteractionMode.SystemAudio;
        State = AppState.Listening;
        _sessionCts = new CancellationTokenSource();
        ContinuousSessionStarted?.Invoke();

        lock (_continuousSegment) _continuousSegment.Clear();
        _continuousHasSpeech = false;
        _continuousLastLoud = DateTime.MinValue;

        _continuousCalibrating = true;
        _continuousCalibrationStart = DateTime.UtcNow;
        _continuousCalibrationMin = float.MaxValue;
        _continuousVadThreshold = ContinuousVadMinThreshold; // fallback until calibration completes

        _continuousTurnChannel = Channel.CreateUnbounded<byte[]>();

        _audio.PowerLevelChanged += OnContinuousLevel;
        _audio.AudioChunkAvailable += OnContinuousAudioChunk;
        _audio.Start(loopback: true);
        Logger.Log("[Audio] continuous capture started");

        _ = ContinuousTurnLoopAsync(_continuousTurnChannel, _sessionCts.Token);
    }

    private async Task StopContinuousSystemAudioAsync()
    {
        Logger.Log("[Hotkey] System Audio continuous mode OFF");
        _continuousAudioActive = false;

        _audio.PowerLevelChanged -= OnContinuousLevel;
        _audio.AudioChunkAvailable -= OnContinuousAudioChunk;
        await StopAudioWithTimeoutAsync();

        _sessionCts?.Cancel();
        _continuousTurnChannel?.Writer.TryComplete();
        _continuousTurnChannel = null;

        lock (_continuousSegment) _continuousSegment.Clear();
        State = AppState.Idle;
    }

    private void OnContinuousLevel(float rms)
    {
        if (_continuousCalibrating)
        {
            if (rms < _continuousCalibrationMin) _continuousCalibrationMin = rms;

            if (DateTime.UtcNow - _continuousCalibrationStart >= ContinuousCalibrationWindow)
            {
                var floor = _continuousCalibrationMin == float.MaxValue ? 0f : _continuousCalibrationMin;
                _continuousVadThreshold = Math.Clamp(
                    floor + ContinuousVadMargin, ContinuousVadMinThreshold, ContinuousVadMaxThreshold);
                _continuousCalibrating = false;
                Logger.Log($"[VAD] Calibrated: noise floor={floor:F4}, threshold={_continuousVadThreshold:F4}");
            }
            return; // don't treat calibration-window audio as a speech trigger either way
        }

        if (rms >= _continuousVadThreshold)
        {
            _continuousLastLoud = DateTime.UtcNow;
            _continuousHasSpeech = true;
        }
    }

    private void OnContinuousAudioChunk(byte[] pcm16)
    {
        lock (_continuousSegment) _continuousSegment.AddRange(pcm16);

        if (!_continuousHasSpeech || _continuousLastLoud == DateTime.MinValue) return;
        if (DateTime.UtcNow - _continuousLastLoud < ContinuousSilenceCut) return;

        byte[] segment;
        lock (_continuousSegment)
        {
            segment = _continuousSegment.ToArray();
            _continuousSegment.Clear();
        }
        _continuousHasSpeech = false;
        _continuousLastLoud = DateTime.MinValue;

        if (segment.Length >= ContinuousMinSegmentBytes)
            _continuousTurnChannel?.Writer.TryWrite(segment);
    }

    /// <summary>Consumes cut turns one at a time so overlapping segments never race each
    /// other into Claude — any turn that lands while one's still being answered just waits
    /// in the channel.</summary>
    private async Task ContinuousTurnLoopAsync(Channel<byte[]> channel, CancellationToken ct)
    {
        try
        {
            await foreach (var clip in channel.Reader.ReadAllAsync(ct))
            {
                State = AppState.Processing;

                string transcript = "";
                try
                {
                    Logger.Log($"[Whisper] Transcribing continuous segment ({clip.Length / 32000.0:F1}s)...");
                    transcript = await _inference.TranscribeAsync(clip, ct);
                    Logger.Info($"Heard (continuous): \"{transcript}\"");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Error($"[Whisper] Continuous transcription error: {ex.Message}");
                }

                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    TranscriptReady?.Invoke(transcript, InteractionMode.SystemAudio);
                    var messageForClaude = $"Answer in under 100 words, like a human talking, using " +
                        $"simple everyday language, no jargon: \"{transcript}\"";
                    await ProcessResponseAsync(messageForClaude, []);
                }

                if (_continuousAudioActive) State = AppState.Listening;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnAudioChunk(byte[] pcm16)
    {
        lock (_audioBuffer) _audioBuffer.AddRange(pcm16);
    }

    // Native capture Stop() calls (WASAPI on Windows, CoreAudio-backed on macOS) are
    // blocking with no cancellation support -- if the native stop call itself ever wedges
    // (driver quirk, device disconnect mid-capture), there's no way to un-stick it. Racing
    // it against a timeout at least keeps the app responsive instead of hanging the whole
    // turn (and every turn after it) forever; the abandoned capture object leaks, but the
    // next Start() creates a fresh one regardless, so the app self-recovers.
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
                         "(likely a stuck native audio call) — abandoning it and continuing. " +
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
            await foreach (var chunk in _inference.StreamResponseAsync(transcript, screenshots, _modeThisTurn, _sessionCts!.Token))
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
                var (cuW, cuH) = ComputerUseResolution.Detect(
                    primaryShot.Bounds.Width, primaryShot.Bounds.Height);

                var resized = _screen.CaptureResized(primaryShot.Bounds, cuW, cuH);
                if (resized != null)
                {
                    var (physX, physY) = await _inference.DetectElementAsync(
                        resized.Base64, detectedPoint.Label,
                        primaryShot.Bounds.Width, primaryShot.Bounds.Height,
                        _sessionCts!.Token);

                    if (physX >= 0)
                    {
                        Logger.Log($"[CU] Precise POINT: ({physX},{physY}) label=\"{detectedPoint.Label}\"");
                        _uiDispatch(() =>
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
                _uiDispatch(() =>
                    PointReceived?.Invoke(detectedPoint.X, detectedPoint.Y, detectedPoint.Label));
            }
        }

        State = AppState.Idle;
    }

    public async ValueTask DisposeAsync()
    {
        _sessionCts?.Cancel();
        _audio.AudioChunkAvailable -= OnAudioChunk;
        _audio.PowerLevelChanged -= OnContinuousLevel;
        _audio.AudioChunkAvailable -= OnContinuousAudioChunk;
        _continuousTurnChannel?.Writer.TryComplete();
        _audio.Dispose();
        await _inference.DisposeAsync();
        _sessionCts?.Dispose();
    }
}
