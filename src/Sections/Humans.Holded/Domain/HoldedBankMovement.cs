using NodaTime;

namespace Humans.Holded.Domain;

/// <summary>One line of a treasury account's bank feed. A sync replaces every row of its treasury inside the window
/// it read: a line Holded no longer lists there is deleted.</summary>
internal sealed class HoldedBankMovement
{
    public string TreasuryAccountId { get; init; } = "";

    /// <summary>Holded's movement id.</summary>
    public string Id { get; init; } = "";

    /// <summary>The booking date, else the value date, as the bank feed gives it.</summary>
    public LocalDate Date { get; set; }

    /// <summary>Signed euros: negative is money leaving the account.</summary>
    public decimal Amount { get; set; }

    public string? Description { get; set; }

    /// <summary>`pending` | `partial` | `reconciled`.</summary>
    public string Status { get; set; } = "";

    public Instant LastSyncedAt { get; set; }
}
