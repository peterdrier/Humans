using Humans.Application.Interfaces;

namespace Humans.Consent.Contracts;

/// <summary>
/// The one consent *write* that happens outside the section: Shell's onboarding widget
/// signs a document from the guided flow, and <c>DevPersonaSeeder</c> signs the outstanding
/// set for a seeded persona. The rest of the section's write surface — the admin
/// legal-document CRUD, the GitHub sync, the version-summary edit — has no outside caller
/// and stays internal.
/// </summary>
public interface IConsentSubmission
{
    /// <summary>
    /// Records an explicit consent for <paramref name="documentVersionId"/>. Refuses a stub
    /// profile (no verified legal name) and a version the user has already signed, in both
    /// cases through <see cref="ConsentSubmitResult.ErrorKey"/> rather than an exception.
    /// Consent records are append-only (design-rules §12).
    /// </summary>
    Task<ConsentSubmitResult> SubmitConsentAsync(
        Guid userId, Guid documentVersionId, bool explicitConsent,
        string ipAddress, string userAgent, CancellationToken ct = default);
}
