namespace ClickyWindows.Models;

public record ConversationTurn(string Role, string Content);

/// <summary>
/// Maintains a rolling window of the last N conversation turns for context.
/// </summary>
public class ConversationHistory
{
    private readonly int _maxTurns;
    private readonly List<ConversationTurn> _turns = new();

    public ConversationHistory(int maxTurns = 10)
    {
        _maxTurns = maxTurns;
    }

    public IReadOnlyList<ConversationTurn> Turns => _turns.AsReadOnly();

    public void AddUserMessage(string content) => Add("user", content);
    public void AddAssistantMessage(string content) => Add("assistant", content);

    private void Add(string role, string content)
    {
        _turns.Add(new ConversationTurn(role, content));
        // Keep only the last _maxTurns entries
        while (_turns.Count > _maxTurns)
            _turns.RemoveAt(0);
    }

    public void Clear() => _turns.Clear();
}
