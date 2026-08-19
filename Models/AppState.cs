namespace ClickyWindows.Models;

public enum AppState
{
    Idle,
    Listening,   // push-to-talk key held, capturing audio
    Processing,  // key released, transcription → Claude in flight
    Speaking,    // reply panel is streaming in text
}

/// <summary>
/// Which push-to-talk hotkey triggered this turn — controls both what's captured
/// (screenshot? which audio source?) and how the reply panel labels the answer.
/// </summary>
public enum InteractionMode
{
    /// <summary>Ctrl+Shift+Space (default): mic input, screenshot captured and sent, Claude can point at something on screen.</summary>
    Action,
    /// <summary>Ctrl+Shift+A (default): mic input, no screenshot -- pure Q&amp;A.</summary>
    Answer,
    /// <summary>Ctrl+Shift+R (default): system audio (WASAPI loopback) input instead of the mic, no screenshot -- react to whatever's playing.</summary>
    SystemAudio,
    /// <summary>Ctrl+Shift+Q (default): no mic at all -- a single tap captures one screenshot and asks
    /// Claude to answer every question visible in it. Fires on key press, doesn't need a hold/release.</summary>
    ScreenshotQA,
}
