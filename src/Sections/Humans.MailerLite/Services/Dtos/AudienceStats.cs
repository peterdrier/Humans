using NodaTime;

namespace Humans.MailerLite.Services.Dtos;

internal sealed record AudienceStats(
    string Key,
    string MailerLiteGroupName,
    int Candidates,
    int ExcludedUnsubscribed,
    int CurrentlyInGroup,
    Instant? LastSyncAt,
    string? LastSyncSummary);
