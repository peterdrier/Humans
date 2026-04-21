using NodaTime;

namespace Humans.Domain.Entities;

public class AgentConversation
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }
    public User User { get; set; } = null!;

    public Instant StartedAt { get; init; }
    public Instant LastMessageAt { get; set; }

    /// <summary>BCP-47 tag, e.g. "es", "ca", "en". Captured at conversation start.</summary>
    public string Locale { get; set; } = "es";

    public int MessageCount { get; set; }

    public ICollection<AgentMessage> Messages { get; set; } = new List<AgentMessage>();
}
