using System.Diagnostics;
using ClickyWindows.Helpers;
using ClickyWindows.Services;

namespace ClickyMac.Services;

/// <summary>
/// Captures audio via `sox` (Homebrew: `brew install sox`) rather than a native CoreAudio
/// binding -- macOS has no P/Invoke-able equivalent of WASAPI that a single small binding
/// could cover for both microphone and loopback capture, and sox already does exactly the
/// PCM16/16kHz/mono conversion Whisper needs, so there's no resampling code needed here at
/// all (unlike the Windows build's manual float32-to-PCM16 conversion). Requires sox to be
/// on PATH or in a common Homebrew location -- see Clicky.Mac/README.md.
///
/// System Audio mode: macOS has no OS-level loopback API (unlike WASAPI). This captures
/// from a named CoreAudio input device instead -- set AppSettings.SystemAudioInputDeviceName
/// to a virtual audio device such as BlackHole (`brew install blackhole-2ch`), and route
/// your system output to it. Without that configured, System Audio mode captures the
/// microphone same as Answer mode, which isn't useful but won't crash.
///
/// UNTESTED: no Mac available to verify sox's coreaudio driver behavior firsthand.
/// </summary>
public class MacAudioCaptureService : IAudioCaptureService
{
    private readonly string _systemAudioInputDeviceName;

    public event Action<byte[]>? AudioChunkAvailable;
    public event Action<float>? PowerLevelChanged;

    private Process? _process;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private volatile bool _running;

    // 16kHz, 16-bit, mono PCM -- exactly what LocalWhisperService expects, so sox's output
    // needs zero further conversion. ~100ms per chunk, matching the Windows build's cadence.
    private const int SampleRate = 16000;
    private const int BytesPerChunk = 3200;

    public MacAudioCaptureService(string systemAudioInputDeviceName)
    {
        _systemAudioInputDeviceName = systemAudioInputDeviceName;
    }

    public void Start(bool loopback = false)
    {
        if (_running) return;

        var soxPath = ResolveSoxPath();
        if (soxPath == null)
        {
            Logger.Error("[Audio] sox not found -- install it with `brew install sox` (and `brew install blackhole-2ch` for System Audio mode). Nothing will be captured this turn.");
            return;
        }

        var psi = new ProcessStartInfo(soxPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (loopback && !string.IsNullOrWhiteSpace(_systemAudioInputDeviceName))
        {
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("coreaudio");
            psi.ArgumentList.Add(_systemAudioInputDeviceName);
            Logger.Log($"[Audio] Starting sox capture from CoreAudio device \"{_systemAudioInputDeviceName}\" (System Audio mode)");
        }
        else
        {
            if (loopback)
                Logger.Error("[Audio] System Audio mode but no SystemAudioInputDeviceName configured -- falling back to the microphone (set it to a virtual device like \"BlackHole 2ch\" in Settings).");
            psi.ArgumentList.Add("-d"); // default input device (microphone)
            Logger.Log("[Audio] Starting sox capture from the default input device (microphone)");
        }

        psi.ArgumentList.Add("-q"); // quiet -- keep stderr nearly empty
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("raw");
        psi.ArgumentList.Add("-r"); psi.ArgumentList.Add(SampleRate.ToString());
        psi.ArgumentList.Add("-e"); psi.ArgumentList.Add("signed-integer");
        psi.ArgumentList.Add("-b"); psi.ArgumentList.Add("16");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-"); // stdout

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Audio] Failed to start sox: {ex.Message}");
            return;
        }

        if (_process == null) return;

        _running = true;
        _readCts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_process, _readCts.Token));
        _ = DrainStderrAsync(_process); // best-effort, prevents the pipe buffer from filling and stalling sox
    }

    private void ReadLoop(Process process, CancellationToken ct)
    {
        var stdout = process.StandardOutput.BaseStream;
        var buffer = new byte[BytesPerChunk];

        while (!ct.IsCancellationRequested)
        {
            int offset = 0;
            int read;
            try
            {
                while (offset < buffer.Length &&
                       (read = stdout.Read(buffer, offset, buffer.Length - offset)) > 0)
                {
                    offset += read;
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Logger.Error($"[Audio] sox read error: {ex.Message}");
                return;
            }

            if (offset == 0) return; // stream ended (process exited)

            var chunk = offset == buffer.Length ? (byte[])buffer.Clone() : buffer[..offset];
            PowerLevelChanged?.Invoke(CalculateRms(chunk));
            AudioChunkAvailable?.Invoke(chunk);
        }
    }

    private static async Task DrainStderrAsync(Process process)
    {
        try
        {
            var buffer = new char[256];
            while (await process.StandardError.ReadAsync(buffer) > 0) { }
        }
        catch { }
    }

    private static float CalculateRms(byte[] pcm16)
    {
        if (pcm16.Length < 2) return 0f;
        double sum = 0;
        int count = pcm16.Length / 2;
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            double s = BitConverter.ToInt16(pcm16, i) / 32768.0;
            sum += s * s;
        }
        return Math.Clamp((float)Math.Sqrt(sum / count) * 10f, 0f, 1f);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        var process = _process;
        var cts = _readCts;
        _process = null;
        _readCts = null;

        cts?.Cancel();
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Audio] Error stopping sox: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
            cts?.Dispose();
        }
    }

    private static string? ResolveSoxPath()
    {
        foreach (var candidate in new[] { "/opt/homebrew/bin/sox", "/usr/local/bin/sox", "/usr/bin/sox" })
            if (File.Exists(candidate)) return candidate;

        // Fallback: ask a login shell to resolve PATH -- covers non-default Homebrew
        // prefixes, since a GUI-launched app's own PATH often excludes /opt/homebrew/bin.
        try
        {
            var psi = new ProcessStartInfo("/bin/zsh") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add("which sox");
            using var p = Process.Start(psi);
            if (p == null) return null;
            var path = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            return p.ExitCode == 0 && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => Stop();
}
