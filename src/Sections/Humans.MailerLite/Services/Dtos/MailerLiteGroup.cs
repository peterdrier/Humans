using NodaTime;

namespace Humans.MailerLite.Services.Dtos;

internal sealed record MailerLiteGroup(
    string Id,
    string Name,
    Instant CreatedAt,
    int ActiveCount,
    int UnsubscribedCount,
    int UnconfirmedCount,
    int BouncedCount,
    int JunkCount);
