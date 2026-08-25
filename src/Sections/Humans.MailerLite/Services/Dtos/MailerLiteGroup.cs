namespace Humans.MailerLite.Services.Dtos;

internal sealed record MailerLiteGroup(
    string Id,
    string Name,
    int ActiveCount,
    int UnsubscribedCount,
    int UnconfirmedCount,
    int BouncedCount,
    int JunkCount);
