using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>
/// Created or linked outside the budget map. Which caller wanted it is that caller's business. The registry keeps these
/// accounts' docs off the Unmatched queue and lets a retired account drop out of the pickers.
/// </summary>
internal sealed class HoldedManagedAccount
{
    public Guid Id { get; init; }
    public int HoldedAccountNumber { get; set; }
    public string HoldedAccountId { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
