namespace Humans.Application.Interfaces.Consent;

/// <summary>
/// Consent section's metrics surface (issue nobodies-collective/Humans#580).
/// </summary>
public interface IConsentMetrics
{
    /// <summary>
    /// Increments <c>humans.consents_given_total</c>. Called by
    /// <c>ConsentService</c> on every successful consent-record INSERT.
    /// </summary>
    void RecordConsentGiven();
}
