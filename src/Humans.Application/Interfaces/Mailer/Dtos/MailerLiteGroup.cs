using NodaTime;

namespace Humans.Application.Interfaces.Mailer.Dtos;

public sealed record MailerLiteGroup(
    string Id,
    string Name,
    Instant CreatedAt,
    int ActiveCount,
    int UnsubscribedCount,
    int UnconfirmedCount,
    int BouncedCount,
    int JunkCount);
