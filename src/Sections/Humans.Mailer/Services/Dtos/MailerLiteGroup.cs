using NodaTime;

namespace Humans.Mailer.Services.Dtos;

internal sealed record MailerLiteGroup(
    string Id,
    string Name,
    Instant CreatedAt,
    int ActiveCount,
    int UnsubscribedCount,
    int UnconfirmedCount,
    int BouncedCount,
    int JunkCount);
