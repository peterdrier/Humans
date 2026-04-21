using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

public class AgentMessage
{
    public Guid Id { get; init; }

    public Guid ConversationId { get; init; }
    public AgentConversation Conversation { get; set; } = null!;

    public AgentRole Role { get; set; }
    public string Content { get; set; } = string.Empty;

    public Instant CreatedAt { get; init; }

    public int PromptTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedTokens { get; set; }

    public string Model { get; set; } = string.Empty;
    public int DurationMs { get; set; }

    /// <summary>Tool targets fetched during this turn. Stored as JSON string[].</summary>
    public string[] FetchedDocs { get; set; } = Array.Empty<string>();

    public string? RefusalReason { get; set; }

    public Guid? HandedOffToFeedbackId { get; set; }
    public FeedbackReport? HandedOffToFeedback { get; set; }
}
