using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>Cached 400000xx creditor-account balance from Holded chartofaccounts. Negative balance = owed.</summary>
public class HoldedCreditorBalance
{
    public Guid Id { get; init; }
    public int SupplierAccountNum { get; set; }   // 400000xx, unique
    public string Name { get; set; } = "";
    public decimal Balance { get; set; }          // signed; negative = org owes
    public Instant LastSyncedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
