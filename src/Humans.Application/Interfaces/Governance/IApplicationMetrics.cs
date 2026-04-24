namespace Humans.Application.Interfaces.Governance;

/// <summary>
/// Governance section's metrics surface for tier-application processing
/// (issue nobodies-collective/Humans#580).
/// </summary>
public interface IApplicationMetrics
{
    /// <summary>
    /// Increments <c>humans.applications_processed_total</c> with the
    /// <c>action</c> tag ("approved", "rejected", "withdrawn"). Called by
    /// <c>ApplicationDecisionService</c> after each finalise action.
    /// </summary>
    void RecordApplicationProcessed(string action);
}
