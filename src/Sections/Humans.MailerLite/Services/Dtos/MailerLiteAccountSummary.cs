namespace Humans.MailerLite.Services.Dtos;

/// <summary>Counted while paging the full subscriber list, so there are no per-status requests.</summary>
internal sealed record MailerLiteAccountSummary(
    int ActiveCount,
    int UnsubscribedCount,
    int UnconfirmedCount,
    int BouncedCount,
    int JunkCount);
