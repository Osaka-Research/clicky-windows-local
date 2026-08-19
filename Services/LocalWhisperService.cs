using System.Text;
using ClickyWindows.Helpers;
using Whisper.net;
using Whisper.net.Ggml;

namespace ClickyWindows.Services;

/// <summary>
/// Fully offline speech-to-text via whisper.cpp (Whisper.net bindings). Replaces
/// AssemblyAIService's realtime cloud WebSocket — there's no streaming/turn-detection
/// here, just: buffer the whole push-to-talk clip, transcribe it in one shot on release.
/// Downloads a ggml model once on first run (~140MB for the default "base" size) and
/// caches it next to the exe.
/// </summary>
public class LocalWhisperService : IAsyncDisposable
{
    private readonly string _modelPath;
    private readonly GgmlType _ggmlType;
    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public LocalWhisperService(string modelSize)
    {
        _ggmlType = ParseGgmlType(modelSize);
        var dir = Path.Combine(AppContext.BaseDirectory, "whisper-models");
        Directory.CreateDirectory(dir);
        _modelPath = Path.Combine(dir, $"ggml-{modelSize.ToLowerInvariant()}.bin");
    }

    private static GgmlType ParseGgmlType(string size) => size.ToLowerInvariant() switch
    {
        "tiny" => GgmlType.Tiny,
        "base" => GgmlType.Base,
        "small" => GgmlType.Small,
        "medium" => GgmlType.Medium,
        _ => GgmlType.Base,
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
            {
                Logger.Log($"[Whisper] Downloading model to {_modelPath} (first run only, one-time)...");
                using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(_ggmlType);
                using var fileWriter = File.OpenWrite(_modelPath);
                await modelStream.CopyToAsync(fileWriter);
                Logger.Log("[Whisper] Model downloaded.");
            }

            Logger.Log($"[Whisper] Loading model from {_modelPath}...");
            _factory = WhisperFactory.FromPath(_modelPath);
            Logger.Log("[Whisper] Model loaded.");
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Transcribes a buffer of PCM16 16kHz mono samples (as produced by AudioCaptureService).
    /// Returns "" for silence/near-empty buffers.
    /// </summary>
    public async Task<string> TranscribeAsync(byte[] pcm16Mono16k, CancellationToken ct = default)
    {
        if (pcm16Mono16k.Length < 3200) return ""; // < ~100ms of audio — nothing was said

        await EnsureModelLoadedAsync();
        if (_factory == null) return "";

        using var processor = _factory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        using var wavStream = ToWavStream(pcm16Mono16k);

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(wavStream, ct))
        {
            sb.Append(segment.Text);
        }
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
