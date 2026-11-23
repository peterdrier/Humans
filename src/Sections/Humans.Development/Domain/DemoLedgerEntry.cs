using NodaTime;

namespace Humans.Development.Domain;

internal enum DemoLedgerState
{
    /// <summary>Recorded before the create, so a run cut short finds the planned id instead of making a twin.</summary>
    Pending,
    Made,
    Retired,
    LeftBehind,
}

/// <summary>One row the demo made in another section, by id only: the ledger holds no personal data.</summary>
internal sealed class DemoLedgerEntry
{
    /// <summary>Derived from <see cref="Key"/> and <see cref="Generation"/>, so identity is an id, never the key.</summary>
    public Guid Id { get; init; }

    public string Key { get; init; } = string.Empty;
    public int Generation { get; init; }
    public Guid EntityId { get; set; }
    public DemoLedgerState State { get; set; }
    public string? Note { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
