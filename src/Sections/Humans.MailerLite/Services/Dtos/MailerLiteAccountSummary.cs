namespace Humans.MailerLite.Services.Dtos;

/// <summary>
/// Global per-status totals, counted in process while the client pages the full
/// subscriber list once. No per-status request fan-out — the snapshot is already
/// being pulled, so the buckets come free.
/// </summary>
internal sealed record MailerLiteAccountSummary(
    int ActiveCount,
    int UnsubscribedCount,
    int UnconfirmedCount,
    int BouncedCount,
    int JunkCount);
