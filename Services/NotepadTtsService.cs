using System.Diagnostics;
using ClickyWindows.Helpers;

namespace ClickyWindows.Services;

/// <summary>
/// Stands in for ElevenLabsService — instead of synthesizing and playing audio, writes
/// Claude's response to a text file and opens it in Notepad. No network call, no
/// playback duration to await; "speaking" here just means the window popped up.
/// Mirrors ElevenLabsService's PlaybackStarting event so CompanionManager needs no
/// changes to its state-transition timing.
/// </summary>
public class NotepadTtsService : IDisposable
{
    private readonly string _filePath;
    private Process? _notepadProcess;

    public event Action? PlaybackStarting;

    public NotepadTtsService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ClickyWindowsLocal");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "response.txt");
    }

    /// <summary>Writes [text] to the response file and opens/refreshes it in Notepad.</summary>
    public Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;

        // Close the previous response window (if we still own it) so the new text
        // is what's actually on screen — Notepad doesn't live-reload an open file.
        StopPlayback();

        File.WriteAllText(_filePath, text);

        PlaybackStarting?.Invoke();

        try
        {
            _notepadProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{_filePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"[Notepad] Failed to open: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public void StopPlayback()
    {
        if (_notepadProcess != null && !_notepadProcess.HasExited)
        {
            try { _notepadProcess.CloseMainWindow(); }
            catch { /* user may have already closed it, or it prompts to save — fine either way */ }
        }
        _notepadProcess = null;
    }

    public void Dispose() => StopPlayback();
}
