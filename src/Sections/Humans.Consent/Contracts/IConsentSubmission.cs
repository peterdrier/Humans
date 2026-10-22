namespace Humans.Consent.Contracts;

public interface IConsentSubmission
{
    /// <summary>
    /// Refuses a stub profile, an already-signed version or unavailable canonical Spanish content through
    /// <see cref="ConsentSubmitResult.ErrorKey"/>, not an exception.
    /// </summary>
    Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default);
}
