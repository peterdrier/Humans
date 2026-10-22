namespace Humans.Holded;

/// <summary>Separate from the connector's <c>HoldedClientOptions</c>, which the mirror has no business reading.</summary>
internal sealed class HoldedSectionOptions
{
    /// <summary>
    /// The plan-tier allowance (~2,000/month on Basic), which the API doesn't report: GET /usage's <c>limit</c> is
    /// the billable-overage ceiling (2,000,000), so budgeting against it would read as unlimited.
    /// </summary>
    public int MonthlyCallBudget { get; set; } = 2000;
}
