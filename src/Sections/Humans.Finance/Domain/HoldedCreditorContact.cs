using Humans.Finance.Contracts;
using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>One per member: created on the first expense push, or set by a finance admin before any push.</summary>
internal sealed class HoldedCreditorContact
{
    public Guid Id { get; init; }

    /// <summary>Unique.</summary>
    public Guid UserId { get; set; }

    /// <summary>The stable key for every push and ledger or payment lookup.</summary>
    public string HoldedContactId { get; set; } = "";

    /// <summary>supplierRecord.num. Null until Holded assigns it.</summary>
    public int? SupplierAccountNum { get; set; }

    public CreditorContactSource Source { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
