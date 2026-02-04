namespace Profiles.Application.DTOs;

/// <summary>
/// Request to record consent for a document version.
/// </summary>
public record ConsentRequest(
    Guid DocumentVersionId,
    bool ExplicitConsent,
    string IpAddress,
    string UserAgent);
