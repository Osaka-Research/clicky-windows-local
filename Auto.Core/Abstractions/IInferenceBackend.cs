namespace Auto.Services;

/// <summary>
/// The transcription + reasoning half of a turn: audio in, transcript out; transcript
/// (+ screenshots) in, streamed reply text out. CompanionManager talks only to this
/// interface, never to Whisper or Claude directly, so it doesn't care whether either step
/// runs on this machine (LocalInferenceBackend) or on a remote server
/// (RemoteInferenceBackend) — the only difference is which one a client is configured to
/// use. This is what makes "no GPU? use a server instead" a settings toggle rather than a
/// different app.
/// </summary>
public interface IInferenceBackend : IAsyncDisposable
{
    /// <summary>Transcribes a buffer of PCM16 16kHz mono samples. Returns "" for silence.</summary>
    Task<string> TranscribeAsync(byte[] pcm16Mono16k, CancellationToken ct = default);

    /// <summary>Sends transcript + screenshots to Claude and streams back the response text.</summary>
    IAsyncEnumerable<string> StreamResponseAsync(
        string transcript, List<ScreenshotResult> screenshots, Models.InteractionMode mode,
        CancellationToken ct = default);

    /// <summary>
    /// Refines a rough [POINT:...] location into a precise one via Claude's Computer Use
    /// tool. Returns (-1, -1) if the element couldn't be found.
    /// </summary>
    Task<(int physX, int physY)> DetectElementAsync(
        string base64Jpeg, string elementDescription, int screenWidth, int screenHeight,
        CancellationToken ct = default);
}
