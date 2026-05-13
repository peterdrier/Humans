using NodaTime;

namespace Humans.Application.Interfaces.Mailer.Dtos;

/// <summary>Read-only stats for one audience, shown on the Mailer admin dashboard.</summary>
public sealed record AudienceStats(
    string Key,
    string DisplayName,
    string MailerLiteGroupName,
    int Candidates,
    int ExcludedUnsubscribed,
    int CurrentlyInGroup,
    Instant? LastSyncAt,
    string? LastSyncSummary);
