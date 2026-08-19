using System.Speech.Synthesis;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// Real spoken output via SAPI (System.Speech.Synthesis) — Windows' built-in speech
/// engine. Fully offline, zero model download, uses whatever voice is installed/selected
/// in Windows Settings > Time &amp; Language > Speech. Replaces NotepadTtsService.
/// Same shape as ElevenLabsService (SpeakAsync/StopPlayback/PlaybackStarting) so
/// CompanionManager needs no other changes.
/// </summary>
public class SapiTtsService : IDisposable
{
    private readonly SpeechSynthesizer _synth = new();

    public event Action? PlaybackStarting;

    public SapiTtsService()
    {
        _synth.SetOutputToDefaultAudioDevice();

        // Prefer a natural-sounding installed voice over the oldest default ones, if present.
        var preferred = _synth.GetInstalledVoices()
            .Select(v => v.VoiceInfo)
            .FirstOrDefault(v => v.Name.Contains("Zira") || v.Name.Contains("Aria") || v.Name.Contains("Jenny"));
        if (preferred != null)
        {
            _synth.SelectVoice(preferred.Name);
            Logger.Log($"[TTS] Using voice: {preferred.Name}");
        }
        else
        {
            Logger.Log($"[TTS] Using default voice: {_synth.Voice.Name}");
        }
    }

    /// <summary>Speaks [text] and suspends until playback completes (or is cancelled).</summary>
    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var tcs = new TaskCompletionSource();
        EventHandler<SpeakCompletedEventArgs>? handler = null;
        handler = (_, _) =>
        {
            _synth.SpeakCompleted -= handler;
            tcs.TrySetResult();
        };
        _synth.SpeakCompleted += handler;

        using var reg = ct.Register(() =>
        {
            _synth.SpeakAsyncCancelAll();
            tcs.TrySetCanceled();
        });

        PlaybackStarting?.Invoke();
        _synth.SpeakAsync(text);
        await tcs.Task;
    }

    public void StopPlayback() => _synth.SpeakAsyncCancelAll();

    public void Dispose() => _synth.Dispose();
}
