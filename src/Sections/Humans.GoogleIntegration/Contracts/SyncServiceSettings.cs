using NodaTime;

namespace Humans.GoogleIntegration.Contracts;

public class SyncServiceSettings
{
    public Guid Id { get; init; }

    public SyncServiceType ServiceType { get; init; }

    public SyncMode SyncMode { get; set; } = SyncMode.None;

    public Instant UpdatedAt { get; set; }

    /// <summary>Null for seed data.</summary>
    public Guid? UpdatedByUserId { get; set; }
}
