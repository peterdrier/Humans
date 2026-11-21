using NodaTime;

namespace Humans.Google.Domain;

internal sealed class SyncServiceSettings
{
    public Guid Id { get; init; }

    public SyncServiceType ServiceType { get; init; }

    public SyncMode SyncMode { get; set; } = SyncMode.None;

    public Instant UpdatedAt { get; set; }

    /// <summary>Null for seed data.</summary>
    public Guid? UpdatedByUserId { get; set; }
}
