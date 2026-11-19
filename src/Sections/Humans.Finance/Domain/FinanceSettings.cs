namespace Humans.Finance.Domain;

/// <summary>One row (<see cref="SingletonId"/>), edited on /Settings#finance. Unset values switch off what needs
/// them; nothing is inferred.</summary>
internal sealed class FinanceSettings
{
    public const int SingletonId = 1;

    public int Id { get; init; } = SingletonId;

    /// <summary>The Holded contact every Pleo purchase is booked on ("Pleo card purchases"). Unset: no Pleo booking.</summary>
    public string? PleoHoldedContactId { get; set; }

    /// <summary>The Holded treasury (57200003, the Pleo wallet) every Pleo purchase is paid from. Unset: no Pleo
    /// booking.</summary>
    public string? PleoHoldedTreasuryId { get; set; }
}
