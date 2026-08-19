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
    private readonly ConversationHistory _history;

    private CancellationTokenSource? _sessionCts;
    private readonly List<byte> _audioBuffer = new();
    private bool _includeScreenThisTurn = true;

    public event Action<AppState>? StateChanged;
    public event Action<double, double, string>? PointReceived;
    public event Action<float>? AudioLevelChanged;
    /// <summary>Fires with a short message when a pipeline stage fails silently.</summary>
    public event Action<string>? FeedbackReceived;
    /// <summary>Fires once the mic has stopped and transcription is about to start — triggers the spinner pulse.</summary>
    public event Action? TranscriptConfirmed;
    /// <summary>Fires as soon as Whisper produces a transcript — UI should show the reply panel with
    /// this text, before Claude has even started replying. The bool is true for Action mode
    /// (screen was shared), false for Answer mode (it wasn't).</summary>
    public event Action<string, bool>? TranscriptReady;
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
        _history = new ConversationHistory(maxTurns: 10);
        _audio = new AudioCaptureService();
        _screen = new ScreenCaptureService();
        _claude = new ClaudeService(settings, _history);
        _whisper = new LocalWhisperService(settings.WhisperModelSize);

        _audio.PowerLevelChanged += level => AudioLevelChanged?.Invoke(level);

        // Kick off the (potentially slow, first-run-only) model download/load in the
        // background at startup so the first push-to-talk isn't the one paying for it.
        _ = _whisper.EnsureModelLoadedAsync();
    }

    // ── Push-to-talk lifecycle ──────────────────────────────────────────────

    /// <param name="includeScreen">
    /// True = Action mode (default): a screenshot is captured and sent, Claude can point
    /// at something on screen. False = Answer mode: no screenshot is captured or sent at
    /// all, pure Q&amp;A -- for when you don't want the current screen shared.
    /// </param>
    public Task OnPushToTalkPressed(bool includeScreen = true)
    {
        // Allow pressing hotkey while a reply is still streaming in — dismiss it and listen again
        if (State == AppState.Speaking)
        {
            Logger.Log("[Hotkey] Interrupting — dismissing in-progress reply");
            _sessionCts?.Cancel();
            ReplyDismissed?.Invoke();
            _state = AppState.Idle; // set directly to avoid double-firing state events
        }

        if (State != AppState.Idle)
        {
            Logger.Log($"[Hotkey] Pressed but state={State} — ignoring");
            return Task.CompletedTask;
        }

        Logger.Log($"[Hotkey] Push-to-talk PRESSED (mode={(includeScreen ? "Action" : "Answer")})");
        _includeScreenThisTurn = includeScreen;
        State = AppState.Listening;
        _sessionCts = new CancellationTokenSource();

        lock (_audioBuffer) _audioBuffer.Clear();
        _audio.AudioChunkAvailable += OnAudioChunk;
        _audio.Start();
        Logger.Log("[Audio] WASAPI capture started");

        return Task.CompletedTask;
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
        _audio.Stop();
        Logger.Log("[Audio] Capture stopped");

        byte[] clip;
        lock (_audioBuffer) clip = _audioBuffer.ToArray();

        // Capture screens while Whisper is (about to be) working — same as the cloud build.
        // Skipped entirely in Answer mode: nothing is captured, nothing is sent.
        List<ScreenshotResult> screenshots = [];
        if (_includeScreenThisTurn)
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
            Logger.Log("[Screen] Answer mode — no screenshot captured");
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
            TranscriptReady?.Invoke(transcript, _includeScreenThisTurn);
            await ProcessResponseAsync(transcript, screenshots);
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
            await foreach (var chunk in _claude.StreamResponseAsync(transcript, screenshots, _sessionCts!.Token))
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
