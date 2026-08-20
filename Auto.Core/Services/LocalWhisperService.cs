using System.Text;
using Auto.Helpers;
using Whisper.net;
using Whisper.net.Ggml;

namespace Auto.Services;

/// <summary>
/// Fully offline speech-to-text via whisper.cpp (Whisper.net bindings). There's no
/// streaming/turn-detection here, just: buffer the whole push-to-talk clip, transcribe
/// it in one shot on release. Downloads a ggml model once on first run (~140MB for the
/// default "base.en" size) and caches it under the app-data folder (survives
/// rebuilds/reinstalls, unlike caching next to the executable).
/// </summary>
public class LocalWhisperService : IAsyncDisposable
{
    private readonly string _modelPath;
    private readonly GgmlType _ggmlType;
    private readonly bool _englishOnly;
    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public LocalWhisperService(string modelSize)
    {
        _ggmlType = ParseGgmlType(modelSize);
        _englishOnly = modelSize.Trim().EndsWith(".en", StringComparison.OrdinalIgnoreCase);

        // The app-data folder, not next to the executable: the build output folder gets
        // wiped/replaced on every rebuild or reinstall, which would silently force a
        // ~140MB re-download (and a multi-second stall mid-conversation) every time.
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppPaths.AppFolderName, "whisper-models");
        Directory.CreateDirectory(dir);
        _modelPath = Path.Combine(dir, $"ggml-{modelSize.ToLowerInvariant()}.bin");
    }

    // English-only ("<size>.en") models are more accurate than the multilingual ones at
    // the same size, at the cost of only understanding English speech.
    private static GgmlType ParseGgmlType(string size) => size.Trim().ToLowerInvariant() switch
    {
        "tiny" => GgmlType.Tiny,
        "tiny.en" => GgmlType.TinyEn,
        "base" => GgmlType.Base,
        "base.en" => GgmlType.BaseEn,
        "small" => GgmlType.Small,
        "small.en" => GgmlType.SmallEn,
        "medium" => GgmlType.Medium,
        "medium.en" => GgmlType.MediumEn,
        _ => GgmlType.BaseEn,
    };

    /// <summary>Downloads the model (first run only) and loads it. Safe to call repeatedly.</summary>
    public async Task EnsureModelLoadedAsync()
    {
        if (_factory != null) return;

        await _loadLock.WaitAsync();
        try
        {
            if (_factory != null) return;

            if (!File.Exists(_modelPath))
                await DownloadModelAsync();

            try
            {
                Logger.Log($"[Whisper] Loading model from {_modelPath}...");
                _factory = WhisperFactory.FromPath(_modelPath);
                Logger.Log("[Whisper] Model loaded.");
            }
            catch (Exception ex)
            {
                // File exists but won't load -- almost always a truncated/corrupt download
                // (e.g. the app got killed mid-download). Delete it and try once more; a
                // fresh full download should fix it.
                Logger.Error($"[Whisper] Model at {_modelPath} failed to load ({ex.Message}) — " +
                             "assuming it's corrupt/truncated, deleting and re-downloading.");
                File.Delete(_modelPath);
                await DownloadModelAsync();
                _factory = WhisperFactory.FromPath(_modelPath);
                Logger.Log("[Whisper] Model loaded after re-download.");
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Downloads to a temp file first, then moves it into place only once the full copy
    /// succeeds -- so a download interrupted partway (app killed, network drop) never
    /// leaves a file at [_modelPath] that a later run would wrongly trust as complete.
    /// </summary>
    private async Task DownloadModelAsync()
    {
        var tempPath = _modelPath + ".download";
        Logger.Log($"[Whisper] Downloading model to {_modelPath} (one-time, can take a while for larger sizes)...");
        using (var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(_ggmlType))
        using (var fileWriter = File.Create(tempPath))
        {
            await modelStream.CopyToAsync(fileWriter);
        }
        File.Move(tempPath, _modelPath, overwrite: true);
        Logger.Log("[Whisper] Model downloaded.");
    }

    // Hard ceiling on a single transcription — protects against a stuck native call
    // (e.g. a CUDA kernel/driver stall) hanging the whole app forever instead of just
    // failing that one turn. Whisper.net checks the token between segments, so this can
    // only actually preempt at a segment boundary; if the native call is wedged inside a
    // single segment it still won't return, but this at least caps how long we *wait*.
    private static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Transcribes a buffer of PCM16 16kHz mono samples (as produced by IAudioCaptureService).
    /// Returns "" for silence/near-empty buffers, or if it times out.
    /// </summary>
    public async Task<string> TranscribeAsync(byte[] pcm16Mono16k, CancellationToken ct = default)
    {
        if (pcm16Mono16k.Length < 3200) return ""; // < ~100ms of audio — nothing was said

        await EnsureModelLoadedAsync();
        if (_factory == null) return "";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TranscribeTimeout);

        using var processor = _factory.CreateBuilder()
            .WithLanguage(_englishOnly ? "en" : "auto")
            .Build();

        using var wavStream = ToWavStream(pcm16Mono16k);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sb = new StringBuilder();
        try
        {
            await foreach (var segment in processor.ProcessAsync(wavStream, timeoutCts.Token))
            {
                sb.Append(segment.Text);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own timeout fired, not an external cancellation (hotkey interrupt).
            Logger.Error($"[Whisper] Timed out after {TranscribeTimeout.TotalSeconds:F0}s (elapsed {sw.Elapsed.TotalSeconds:F1}s) — " +
                         "possible GPU/driver stall. Treating as no speech heard this turn.");
            return "";
        }

        Logger.Log($"[Whisper] Transcribed in {sw.Elapsed.TotalSeconds:F1}s");
        return sb.ToString().Trim();
    }

    /// <summary>Wraps raw PCM16 16kHz mono bytes in a minimal WAV header — Whisper.net reads WAV streams.</summary>
    private static MemoryStream ToWavStream(byte[] pcm16)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        const int sampleRate = 16000;
        const short channels = 1;
        const short bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm16.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm16.Length);
        writer.Write(pcm16);
        writer.Flush();

        stream.Position = 0;
        return stream;
    }

    public ValueTask DisposeAsync()
    {
        _factory?.Dispose();
        _loadLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
