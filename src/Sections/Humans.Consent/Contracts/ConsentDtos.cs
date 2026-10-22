using NodaTime;

namespace Humans.Consent.Contracts;

/// <summary>
/// <c>ErrorKey</c> is one of <c>ExplicitConsentRequired</c>, <c>StubProfile</c>, <c>NotFound</c>,
/// <c>AlreadyConsented</c>, <c>ContentUnavailable</c>, for the caller to map to a localized message.
/// </summary>
public sealed record ConsentSubmitResult(bool Success, string? DocumentName = null, string? ErrorKey = null);

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

public sealed record RequiredConsentRow(Guid DocumentVersionId, string Title, bool Signed);
