namespace Humans.Users.Domain;

/// <summary>The pre-anonymization values the confirmation email and audit need.</summary>
/// <param name="OriginalEmail">Null when the account never had an email.</param>
/// <param name="OriginalBurnerName">Before anonymization.</param>
/// <param name="PreferredLanguage">For the confirmation email.</param>
internal sealed record AnonymizedAccountSummary(
    string? OriginalEmail,
    string OriginalBurnerName,
    string PreferredLanguage);
