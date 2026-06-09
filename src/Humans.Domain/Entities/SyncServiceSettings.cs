using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Per-service sync mode configuration. Controls what automated sync jobs do.
/// </summary>
public class SyncServiceSettings
{
    public Guid Id { get; init; }

    /// <summary>Which external service this setting applies to.</summary>
    public SyncServiceType ServiceType { get; init; }

    /// <summary>Current sync mode for automated jobs.</summary>
    public SyncMode SyncMode { get; set; } = SyncMode.None;

    /// <summary>When the mode was last changed.</summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>Who last changed the mode. Null for seed data.</summary>
    public Guid? UpdatedByUserId { get; set; }
}
