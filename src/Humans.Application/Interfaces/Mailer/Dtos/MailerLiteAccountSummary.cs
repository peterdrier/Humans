namespace Humans.Application.Interfaces.Mailer.Dtos;

/// <summary>
/// Global per-status totals. Derived by fan-out: one
/// <c>GET /api/subscribers?filter[status]=X&amp;limit=1</c> per bucket;
/// <c>meta.total</c> is read from each response.
/// </summary>
public sealed record MailerLiteAccountSummary(
    int ActiveCount,
    int UnsubscribedCount,
    int UnconfirmedCount,
    int BouncedCount,
    int JunkCount);
