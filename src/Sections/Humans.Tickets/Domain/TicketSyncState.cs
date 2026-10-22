using NodaTime;

namespace Humans.Tickets.Domain;

internal sealed class TicketSyncState
{
    /// <summary>Always 1.</summary>
    public int Id { get; init; }

    public string VendorEventId { get; set; } = string.Empty;

    /// <summary>The start of the last successful sync; the vendor resume cursor. Null forces a full resync.</summary>
    public Instant? LastSyncAt { get; set; }

    public TicketSyncStatus SyncStatus { get; set; }

    public string? LastError { get; set; }

    public Instant? StatusChangedAt { get; set; }
}
