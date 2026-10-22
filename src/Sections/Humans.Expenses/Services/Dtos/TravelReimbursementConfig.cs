namespace Humans.Expenses.Services.Dtos;

/// <summary>2026 Spanish IRPF tax-exempt limits. The defaults are live values, so no configuration is required.</summary>
internal sealed class TravelReimbursementConfig
{
    /// <summary>€/km. Orden HFP/792/2023 raised this from 0.19 to 0.26 in Jul-2023; unchanged for 2026.</summary>
    public decimal MileageRatePerKm { get; set; } = 0.26m;

    /// <summary>€/day: manutención sin pernocta (day trip, within Spain).</summary>
    public decimal PerDiemDayTripRate { get; set; } = 26.67m;

    /// <summary>€/day: manutención con pernocta (overnight, within Spain).</summary>
    public decimal PerDiemOvernightRate { get; set; } = 53.34m;
}
