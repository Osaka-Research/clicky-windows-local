namespace ClickyWindows.Services;

/// <summary>
/// Captures audio and delivers it as PCM16 16kHz mono chunks — the format Whisper
/// expects. Each platform implements this over its own native audio API (WASAPI on
/// Windows, a CoreAudio-backed capture on macOS); CompanionManager only ever talks to
/// this interface, so the push-to-talk/session logic is identical on both platforms.
/// </summary>
public interface IAudioCaptureService : IDisposable
{
    event Action<byte[]>? AudioChunkAvailable;
    event Action<float>? PowerLevelChanged;

    /// <param name="loopback">
    /// True for System Audio mode: capture whatever's playing through the system's
    /// output instead of the microphone. Native on Windows (WASAPI loopback); on macOS
    /// there's no OS-level loopback, so this captures from a configured virtual input
    /// device instead (see AppSettings.SystemAudioInputDeviceName).
    /// </param>
    void Start(bool loopback = false);
    void Stop();
}
