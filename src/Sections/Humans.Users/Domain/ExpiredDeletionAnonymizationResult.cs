namespace Humans.Users.Domain;

/// <summary>The pre-anonymization values the confirmation email and audit need.</summary>
/// <param name="OriginalBurnerName">The confirmation email greets them by it.</param>
internal sealed record ExpiredDeletionAnonymizationResult(
    string OriginalBurnerName);
