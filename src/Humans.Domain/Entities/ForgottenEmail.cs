using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// GDPR-anonymization tombstone. One row per email address that belonged to
/// a now-anonymized User. Stored as a hash so the right-to-deletion guarantee
/// survives the skip-list. Read by Mailer's import classifier to prevent ML
/// re-incarnating a deleted user. Append-only; no update path. Written by
/// AccountDeletionService.AnonymizeExpiredAccountAsync via IForgottenEmailService.
/// </summary>
public class ForgottenEmail
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public string EmailHash { get; init; } = string.Empty;
    public Instant AnonymizedAt { get; init; }
}
