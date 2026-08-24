using Humans.Agent.Services;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Agent.Contracts;

/// <summary>
/// Agent's read-only transcript surface for the machine API — the admin listing and
/// single-conversation fetch behind <c>/api/backdoor/agent</c>
/// (nobodies-collective/Humans#1128). Everything else <c>IAgentService</c> does (asking a
/// turn, the user-facing viewer, the prompt preview) stays internal.
/// </summary>
public interface IAgentTranscriptRead : IApplicationService
{
    /// <summary>
    /// Every conversation across users, messages eagerly loaded so the caller can compute
    /// per-conversation aggregates without N+1 round trips.
    /// </summary>
    Task<IReadOnlyList<AgentConversationTranscriptSnapshot>> ListAllConversationsForAdminWithMessagesAsync(
        bool refusalsOnly, bool handoffsOnly, Guid? userId, int take, int skip,
        CancellationToken cancellationToken);

    /// <summary>One conversation with messages eagerly loaded, or null.</summary>
    Task<AgentConversationTranscriptSnapshot?> GetConversationForAdminAsync(
        Guid id, CancellationToken cancellationToken);
}

/// <summary>A conversation and its full ordered message list.</summary>
public sealed record AgentConversationTranscriptSnapshot(
    Guid Id,
    Guid UserId,
    string Locale,
    Instant StartedAt,
    Instant LastMessageAt,
    int MessageCount,
    IReadOnlyList<AgentMessageSnapshot> Messages);

/// <summary>One message in a transcript.</summary>
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
    /// Handoff = legacy server-side FeedbackReport link, or a successful <c>route_to_issue</c>
    /// recorded in <see cref="FetchedDocs"/> at save time (the propose-only flow never sets
    /// <see cref="HandedOffToFeedbackId"/> — nobodies-collective/Humans#931). Computed here
    /// rather than by the caller so the tool-name vocabulary stays inside the section; mirrors
    /// the <c>handoffsOnly</c> filter in <c>AgentRepository</c>.
    /// </summary>
    public bool IsHandoff =>
        HandedOffToFeedbackId is not null
        || FetchedDocs.Contains(AgentToolNames.RouteToIssue, StringComparer.Ordinal);
}
