using NodaTime;

namespace Humans.MailerLite.Services.Dtos;

internal sealed record MailerLiteSyncSnapshot(Instant LastSyncAt, string Summary);
