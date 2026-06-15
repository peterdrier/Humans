using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Binds a Humans member to their Holded creditor contact (the 400000xx supplier account).
/// One binding per member. Created automatically on the member's first expense-report push, or
/// set manually by a finance admin (e.g. to point at a pre-existing Holded contact) before any push.
/// </summary>
public class HoldedCreditorContact
{
    public Guid Id { get; init; }

    /// <summary>The Humans member this binding belongs to. Bare FK (no nav); unique.</summary>
    public Guid UserId { get; set; }

    /// <summary>Holded contact id (the stable key used for every push and ledger/payment lookup).</summary>
    public string HoldedContactId { get; set; } = "";

    /// <summary>Resolved 400000xx supplier-account number (supplierRecord.num). Null until Holded assigns it.</summary>
    public int? SupplierAccountNum { get; set; }

    public CreditorContactSource Source { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
