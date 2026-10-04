using NodaTime;

namespace Humans.Consent.Contracts;

/// <summary>
/// Outcome of a consent submit. <c>ErrorKey</c> is a stable token the caller maps to a
/// localized message — <c>StubProfile</c>, <c>NotFound</c>, <c>AlreadyConsented</c> —
/// which is why both the section's own controller and Shell's onboarding widget can
/// render the same failures without seeing any section type.
/// </summary>
public sealed record ConsentSubmitResult(bool Success, string? DocumentName = null, string? ErrorKey = null);

/// <summary>
/// A document version rendered for review, with whether this user already signed it.
/// </summary>
public sealed record ConsentReviewDetail(
    Guid DocumentVersionId,
    string DocumentName,
    string VersionNumber,
    IReadOnlyDictionary<string, string> Content,
    Instant EffectiveFrom,
    string? ChangesSummary,
    bool HasAlreadyConsented,
    Instant? ConsentedAt,
    string? UserFullName);

/// <summary>
/// One row in the onboarding-widget Consents step: a single required document
/// (current version) for the user, with whether they have already signed it.
/// </summary>
/// <remarks>
/// The composition rule that builds these rows from the section's document snapshots is
/// <c>RequiredConsentRows.BuildOrdered</c>, internal to the section — it names
/// <c>ActiveRequiredLegalDocumentSnapshot</c>, which has no consumer outside it.
/// </remarks>
public sealed record RequiredConsentRow(Guid DocumentVersionId, string Title, bool Signed);
