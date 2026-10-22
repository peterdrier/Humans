namespace Humans.Tickets.Contracts;

public static class TicketConstants
{
    /// <summary>Per ticket: the part above this is a VAT-free donation; the rest is taxable.</summary>
    public const decimal VipThresholdEuros = 315m;

    /// <summary>Spanish VAT on event tickets, included in the price.</summary>
    public const decimal VatRate = 0.10m;

    /// <summary>The ticket team's shared inbox; transfer requests are emailed here.</summary>
    public const string TicketsTeamEmail = "tickets@nobodies.team";
}
