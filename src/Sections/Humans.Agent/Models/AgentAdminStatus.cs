using Humans.Agent.Contracts;
using NodaTime;

namespace Humans.Agent.Models;

/// <summary>One row per message in the 30-day window; the 24h/7d/30d aggregates are computed in memory.</summary>
internal sealed record AgentStatusMessageRow(
    Guid ConversationId,
    Guid UserId,
    Instant CreatedAt,
    AgentRole Role,
    int PromptTokens,
    int OutputTokens,
    int CachedTokens,
    string Model,
    int DurationMs,
    string[] FetchedDocs,
    string? RefusalReason);

internal sealed record AgentUsageStats(
    int ConversationCount,
    int MessageCount,
    int UniqueUserCount,
    long PromptTokens,
    long OutputTokens,
    long CachedTokens,
    double CacheHitRatio,
    int AverageTurnMs,
    int P95TurnMs);

internal sealed record AgentSpendStats(
    decimal InputUsd,
    decimal OutputUsd,
    decimal CacheReadUsd,
    decimal TotalUsd);

/// <summary>Last 7 days.</summary>
internal sealed record AgentRefusalBucket(string Reason, int Count);

/// <summary>Last 7 days.</summary>
internal sealed record AgentTopDoc(string Slug, int Count);

/// <summary>Last 7 days.</summary>
internal sealed record AgentTopUser(
    Guid UserId,
    int MessageCount,
    int MessagesTodayRemaining,
    int MessagesThisHourRemaining);

/// <summary><see cref="LastRunAt"/> is null until the job has run since process start.</summary>
internal sealed record AgentRetentionRunSnapshot(Instant? LastRunAt, int LastDeletedCount);

internal sealed record AgentBalanceStatus(decimal? BalanceUsd, string? UnavailableReason);
