namespace Humans.Users.Contracts;

/// <summary>
/// Slice returned from <c>IUserRepository.ApplyExpiredDeletionAnonymizationAsync</c>
/// so the service layer can send the confirmation email and log audit entries
/// without re-loading the (now anonymized) user.
/// </summary>
/// <param name="OriginalEmail">
/// The effective email on the account before anonymization (preferring the
/// verified notification-target <c>UserEmail</c> row, falling back to
/// <c>User.Email</c>). May be null when the account never had an email.
/// </param>
/// <param name="OriginalDisplayName">Display name on the user before the write.</param>
/// <param name="PreferredLanguage">Preferred language on the user before the write.</param>
public record ExpiredDeletionAnonymizationResult(
    string? OriginalEmail,
    string OriginalDisplayName,
    string PreferredLanguage);
