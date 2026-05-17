using NodaTime;

namespace Humans.Application.Models;

/// <summary>
/// Flat per-message projection used by the admin status view to compute
/// windowed aggregates in-memory. One row per <c>agent_messages</c> entry
/// inside the 30-day status window. Repository emits these so the
/// Application layer can aggregate without re-querying for each window
/// (24h / 7d / 30d) — at ~500 users and 90-day retention the row count
/// is small enough to fit comfortably in RAM.
/// </summary>
public sealed record AgentStatusMessageRow(
    Guid ConversationId,
    Guid UserId,
    Instant CreatedAt,
    int PromptTokens,
    int OutputTokens,
    int CachedTokens,
    string Model,
    int DurationMs,
    string[] FetchedDocs,
    string? RefusalReason);

/// <summary>Counts and token totals for a single window (24h / 7d / 30d).</summary>
public sealed record AgentUsageStats(
    int ConversationCount,
    int MessageCount,
    int UniqueUserCount,
    long PromptTokens,
    long OutputTokens,
    long CachedTokens,
    double CacheHitRatio,
    int AverageTurnMs,
    int P95TurnMs);

/// <summary>USD spend for a single window, broken into input / output / cache-read components.</summary>
public sealed record AgentSpendStats(
    decimal InputUsd,
    decimal OutputUsd,
    decimal CacheReadUsd,
    decimal TotalUsd);

/// <summary>One bucket in the refusal breakdown (last 7d).</summary>
public sealed record AgentRefusalBucket(string Reason, int Count);

/// <summary>One row of the top-fetched-docs panel (last 7d).</summary>
public sealed record AgentTopDoc(string Slug, int Count);

/// <summary>One row of the top-users panel (last 7d).</summary>
public sealed record AgentTopUser(
    Guid UserId,
    int MessageCount,
    int MessagesTodayRemaining,
    int MessagesThisHourRemaining);

/// <summary>Snapshot of the last retention job run. <see cref="LastRunAt"/>
/// is null until the job has run at least once since process start.</summary>
public sealed record AgentRetentionRunSnapshot(Instant? LastRunAt, int LastDeletedCount);

/// <summary>Either a live balance reading, or the unavailable-with-reason fallback.</summary>
public sealed record AgentBalanceStatus(decimal? BalanceUsd, string? UnavailableReason);
