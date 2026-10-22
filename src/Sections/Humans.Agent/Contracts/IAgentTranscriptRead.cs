using Humans.Agent.Services;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Agent.Contracts;

public interface IAgentTranscriptRead : IApplicationService
{
    /// <summary>Messages eagerly loaded, so per-conversation aggregates need no N+1 round trips.</summary>
    Task<IReadOnlyList<AgentConversationTranscriptSnapshot>> ListAllConversationsForAdminWithMessagesAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken);

    /// <summary>One conversation with messages eagerly loaded, or null.</summary>
    Task<AgentConversationTranscriptSnapshot?> GetConversationForAdminAsync(
        Guid id, CancellationToken cancellationToken);
}

public sealed record AgentConversationTranscriptSnapshot(
    Guid Id,
    Guid UserId,
    string Locale,
    Instant StartedAt,
    Instant LastMessageAt,
    int MessageCount,
    IReadOnlyList<AgentMessageSnapshot> Messages);

public sealed record AgentMessageSnapshot(
    Guid Id,
    Guid ConversationId,
    AgentRole Role,
    string Content,
    Instant CreatedAt,
    int PromptTokens,
    int OutputTokens,
    int CachedTokens,
    string Model,
    int DurationMs,
    string[] FetchedDocs,
    string? RefusalReason,
    Guid? HandedOffToFeedbackId)
{
    /// <summary>
    /// A legacy FeedbackReport link, or a successful <c>route_to_issue</c> recorded in <see cref="FetchedDocs"/>.
    /// Mirrors the <c>handoffsOnly</c> filter in <c>AgentRepository</c>.
    /// </summary>
    public bool IsHandoff =>
        HandedOffToFeedbackId is not null
        || FetchedDocs.Contains(AgentToolNames.RouteToIssue, StringComparer.Ordinal);
}
