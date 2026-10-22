namespace Humans.Users.Contracts;

/// <summary>The pre-anonymization values the confirmation email and audit need.</summary>
/// <param name="OriginalEmail">Verified notification target, else <c>User.Email</c>; null if the account had none.</param>
/// <param name="OriginalBurnerName">The confirmation email greets them by it.</param>
/// <param name="PreferredLanguage">Before the write.</param>
public record ExpiredDeletionAnonymizationResult(
    string? OriginalEmail,
    string OriginalBurnerName,
    string PreferredLanguage);
