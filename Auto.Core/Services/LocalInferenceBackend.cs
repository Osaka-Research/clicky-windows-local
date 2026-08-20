using Auto.Models;
using Auto.Settings;

namespace Auto.Services;

/// <summary>
/// The default backend: local Whisper transcription + a direct Claude API call, exactly
/// what this app has always done. Requires a GPU (or patience) for Whisper and the user's
/// own Anthropic API key. See RemoteInferenceBackend for the no-GPU/no-key alternative.
/// </summary>
public class LocalInferenceBackend : IInferenceBackend
{
    private readonly LocalWhisperService _whisper;
    private readonly ClaudeService _claude;

    public LocalInferenceBackend(AppSettings settings)
    {
        _whisper = new LocalWhisperService(settings.WhisperModelSize);
        _claude = new ClaudeService(settings);

        // Kick off the (potentially slow, first-run-only) model download/load in the
        // background at startup so the first push-to-talk isn't the one paying for it.
        _ = _whisper.EnsureModelLoadedAsync();
    }

    public Task<string> TranscribeAsync(byte[] pcm16Mono16k, CancellationToken ct = default) =>
        _whisper.TranscribeAsync(pcm16Mono16k, ct);

    public IAsyncEnumerable<string> StreamResponseAsync(
        string transcript, List<ScreenshotResult> screenshots, InteractionMode mode,
        CancellationToken ct = default) =>
        _claude.StreamResponseAsync(transcript, screenshots, mode, ct);

    public Task<(int physX, int physY)> DetectElementAsync(
        string base64Jpeg, string elementDescription, int screenWidth, int screenHeight,
        CancellationToken ct = default) =>
        _claude.DetectElementAsync(base64Jpeg, elementDescription, screenWidth, screenHeight, ct);

    public async ValueTask DisposeAsync() => await _whisper.DisposeAsync();
}
