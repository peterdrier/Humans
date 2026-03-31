namespace Humans.Domain.Constants;

/// <summary>
/// Constants for ticket VAT and donation calculations.
/// </summary>
public static class TicketConstants
{
    /// <summary>
    /// VIP ticket threshold in euros. Ticket revenue up to this amount is taxable at the standard rate.
    /// Any amount above this threshold on a single ticket is treated as a VAT-free donation.
    /// </summary>
    public const decimal VipThresholdEuros = 315m;

    /// <summary>
    /// Spanish event ticket VAT rate (10%).
    /// </summary>
    public const decimal VatRate = 0.10m;
}
