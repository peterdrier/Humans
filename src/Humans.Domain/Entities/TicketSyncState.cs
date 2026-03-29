using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Singleton tracking ticket sync operational state.
/// Distinct from SyncServiceSettings which controls sync modes for Google/Discord.
/// This tracks when sync last ran, whether it succeeded, and error details.
/// </summary>
public class TicketSyncState
{
    /// <summary>PK — always 1 (singleton).</summary>
    public int Id { get; init; }

    /// <summary>The vendor event ID currently being synced.</summary>
    public string VendorEventId { get; set; } = string.Empty;

    /// <summary>When the last successful sync completed.</summary>
    public Instant? LastSyncAt { get; set; }

    /// <summary>Current sync status.</summary>
    public TicketSyncStatus SyncStatus { get; set; }

    /// <summary>Error message from last failed sync, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>When SyncStatus last changed.</summary>
    public Instant? StatusChangedAt { get; set; }
}
