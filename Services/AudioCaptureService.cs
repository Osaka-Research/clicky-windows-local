using Auto.Helpers;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Auto.Services;

/// <summary>
/// Captures audio via WASAPI and converts to PCM16 at 16kHz mono — the format Whisper
/// (via LocalWhisperService) expects. Can capture either the microphone or system audio
/// loopback (whatever's currently playing through speakers) — WasapiLoopbackCapture is a
/// WasapiCapture subclass in NAudio, so the rest of the pipeline needs no changes either way.
/// </summary>
public class AudioCaptureService : IAudioCaptureService
{
    private WasapiCapture? _capture;
    private readonly WaveFormat _targetFormat = new WaveFormat(16000, 16, 1);

    public event Action<byte[]>? AudioChunkAvailable;
    public event Action<float>? PowerLevelChanged;

    private bool _running;
    private bool _formatLogged;
    private int _chunksSent;

    public void Start(bool loopback = false)
    {
        if (_running) return;
        _running = true;
        _chunksSent = 0;
        _formatLogged = false;

        _capture = loopback ? new WasapiLoopbackCapture() : new WasapiCapture();
        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();

        Logger.Log($"[Audio] {(loopback ? "WasapiLoopbackCapture (system audio)" : "WasapiCapture (microphone)")} started (device format will be logged on first chunk)");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        // Snapshot and clear the field before the (blocking, uncancellable) native calls
        // below. If a caller abandons this Stop() call after a timeout because it's
        // wedged, and later starts a new capture, that fresh _capture must never get
        // reached by this call finishing late and nulling it back out from under it --
        // operating only on the local reference makes that impossible.
        var capture = _capture;
        _capture = null;

        Logger.Log($"[Audio] Stopping. Total chunks sent to ASR: {_chunksSent}");
        capture?.StopRecording();
        capture?.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _capture == null) return;

        var srcFormat = _capture.WaveFormat;

        // Log source format once so we can diagnose conversion issues
        if (!_formatLogged)
        {
            _formatLogged = true;
            Logger.Log($"[Audio] Source format: {srcFormat.SampleRate}Hz, {srcFormat.Channels}ch, " +
                       $"{srcFormat.BitsPerSample}bit, Encoding={srcFormat.Encoding}");
        }

        // Power level for UI (handles float32 and int16)
        float rms = CalculateRms(e.Buffer, e.BytesRecorded, srcFormat);
        PowerLevelChanged?.Invoke(rms);

        // Convert to PCM16 16kHz mono
        byte[] pcm16 = ConvertToPcm16(e.Buffer, e.BytesRecorded, srcFormat);

        if (pcm16.Length > 0)
        {
            _chunksSent++;
            if (_chunksSent <= 3 || _chunksSent % 50 == 0)
                Logger.Log($"[Audio] Chunk #{_chunksSent}: {e.BytesRecorded} src bytes → {pcm16.Length} PCM16 bytes, RMS={rms:F3}");
            AudioChunkAvailable?.Invoke(pcm16);
        }
        else
        {
            Logger.Log($"[Audio] WARNING: Chunk conversion produced 0 bytes (src={e.BytesRecorded}b, fmt={srcFormat.Encoding})");
        }
    }

    private byte[] ConvertToPcm16(byte[] inputBuffer, int bytesRecorded, WaveFormat srcFormat)
    {
        // Already correct format
        if (srcFormat.SampleRate == 16000 && srcFormat.BitsPerSample == 16 && srcFormat.Channels == 1)
        {
            var copy = new byte[bytesRecorded];
            Buffer.BlockCopy(inputBuffer, 0, copy, 0, bytesRecorded);
            return copy;
        }

        // Common WASAPI path: IEEE float32 (32-bit) at 44100 or 48000Hz, stereo or mono
        if (srcFormat.Encoding == WaveFormatEncoding.IeeeFloat && srcFormat.BitsPerSample == 32)
            return ConvertFloat32ToPcm16(inputBuffer, bytesRecorded, srcFormat.SampleRate, srcFormat.Channels);

        // PCM at wrong rate or channels — use MediaFoundation resampler
        try
        {
            using var ms = new MemoryStream(inputBuffer, 0, bytesRecorded);
            using var srcStream = new RawSourceWaveStream(ms, srcFormat);

            // Convert encoding to PCM first if needed
            WaveStream pcmStream = srcFormat.Encoding != WaveFormatEncoding.Pcm
                ? WaveFormatConversionStream.CreatePcmStream(srcStream)
                : (WaveStream)srcStream;

            using var resampler = new MediaFoundationResampler(pcmStream, _targetFormat)
            {
                ResamplerQuality = 60
            };

            using var output = new MemoryStream();
            byte[] buf = new byte[4096];
            int read;
            while ((read = resampler.Read(buf, 0, buf.Length)) > 0)
                output.Write(buf, 0, read);

            return output.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Log($"[Audio] MediaFoundationResampler failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Manually converts IEEE float32 audio to PCM16 at 16kHz mono.
    /// No external dependencies — avoids MediaFoundation issues.
    /// </summary>
    private static byte[] ConvertFloat32ToPcm16(byte[] src, int byteCount, int srcRate, int srcChannels)
    {
        // Step 1: deinterleave to mono float samples
        int frameCount = byteCount / (4 * srcChannels); // 4 bytes per float32
        var mono = new float[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < srcChannels; ch++)
            {
                int offset = (i * srcChannels + ch) * 4;
                sum += BitConverter.ToSingle(src, offset);
            }
            mono[i] = sum / srcChannels;
        }

        // Step 2: linear resample to 16000Hz
        double ratio = 16000.0 / srcRate;
        int outFrames = (int)(frameCount * ratio);
        var resampled = new float[outFrames];
        for (int i = 0; i < outFrames; i++)
        {
            double srcIdx = i / ratio;
            int lo = (int)srcIdx;
            int hi = Math.Min(lo + 1, frameCount - 1);
            double frac = srcIdx - lo;
            resampled[i] = (float)(mono[lo] * (1.0 - frac) + mono[hi] * frac);
        }

        // Step 3: convert float [-1,1] → int16
        var output = new byte[outFrames * 2];
        for (int i = 0; i < outFrames; i++)
        {
            short s = (short)(Math.Clamp(resampled[i], -1f, 1f) * 32767f);
            output[i * 2]     = (byte)(s & 0xFF);
            output[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return output;
    }

    private static float CalculateRms(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        double sum = 0;
        int count = 0;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            count = bytesRecorded / 4;
            for (int i = 0; i + 3 < bytesRecorded; i += 4)
            {
                float s = BitConverter.ToSingle(buffer, i);
                sum += s * s;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            count = bytesRecorded / 2;
            for (int i = 0; i + 1 < bytesRecorded; i += 2)
            {
                double s = BitConverter.ToInt16(buffer, i) / 32768.0;
                sum += s * s;
            }
        }

        if (count == 0) return 0f;
        return Math.Clamp((float)Math.Sqrt(sum / count) * 10f, 0f, 1f);
    }

    public void Dispose() => Stop();
}
