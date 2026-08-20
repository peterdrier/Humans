using NodaTime;

namespace Humans.MailerLite.Services.Dtos;

/// <summary>Read-only stats for one audience, shown on the MailerLite admin dashboard.</summary>
internal sealed record AudienceStats(
    string Key,
    string DisplayName,
    string MailerLiteGroupName,
    int Candidates,
    int ExcludedUnsubscribed,
    int CurrentlyInGroup,
    Instant? LastSyncAt,
    string? LastSyncSummary);
