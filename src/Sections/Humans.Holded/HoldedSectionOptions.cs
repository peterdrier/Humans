namespace Humans.Holded;

/// <summary>
/// Section-owned settings, bound in <see cref="Section.Register"/> from <c>Holded:*</c>.
/// Separate from the connector's <c>HoldedClientOptions</c> (base URL, API key), which the
/// mirror has no business reading.
/// </summary>
internal sealed class HoldedSectionOptions
{
    /// <summary>
    /// Calls per month the /Holded meter measures against. This is the *plan tier* allowance
    /// (~2,000/month on Basic), which the API does not report: GET /usage's <c>limit</c> is the
    /// billable-overage ceiling (2,000,000), so budgeting against it would read as unlimited.
    /// The screen shows both — Holded's number as information, this one as the budget.
    /// </summary>
    public int MonthlyCallBudget { get; set; } = 2000;
}
