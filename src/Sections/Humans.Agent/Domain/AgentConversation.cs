using NodaTime;
using Humans.Agent.Contracts;

namespace Humans.Agent.Domain;

internal sealed class AgentConversation
{
    /// <summary>Bound prior text turns to the context budget.</summary>
    private const int HistoryReplayLimit = 20;

    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public Instant StartedAt { get; init; }
    public Instant LastMessageAt { get; set; }

    /// <summary>BCP-47 tag, e.g. "es", "ca", "en". Captured at conversation start.</summary>
    public string Locale { get; set; } = "es";

    public int MessageCount { get; set; }

    public ICollection<AgentMessage> Messages { get; set; } = new List<AgentMessage>();

    /// <summary>Text history for the next turn; tool internals are re-derived rather than replayed.</summary>
    public List<AgentMessage> GetReplayableMessages() =>
        Messages
            .Where(m => (m.Role == AgentRole.User || m.Role == AgentRole.Assistant)
                        && !string.IsNullOrEmpty(m.Content))
            .OrderBy(m => m.CreatedAt)
            .TakeLast(HistoryReplayLimit)
            .ToList();
}
