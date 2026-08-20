using NodaTime;

namespace Humans.MailerLite.Services.Dtos;

/// <summary>When a sync last ran and what it did, in prose. The DTO form of a
/// <c>mailerlite_sync_states</c> row for callers outside the section's Data folder.</summary>
internal sealed record MailerLiteSyncSnapshot(Instant LastSyncAt, string Summary);
