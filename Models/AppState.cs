namespace ClickyWindows.Models;

public enum AppState
{
    Idle,
    Listening,   // push-to-talk key held, mic active
    Processing,  // key released, transcription → Claude in flight
    Speaking,    // response window (Notepad) just opened
}
