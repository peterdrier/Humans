using Humans.Application.Interfaces;
using NodaTime;

namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// The single Application-layer surface over <c>forgotten_emails</c>.
/// Written by the account-deletion cascade; read by Mailer import.
/// </summary>
public interface IForgottenEmailService : IApplicationService
{
    /// <summary>
    /// Records every supplied email as forgotten for the given user. Inputs
    /// are hashed via <see cref="Services.Mailer.EmailHasher"/>. Idempotent on
    /// (UserId, EmailHash). Returns the number of rows inserted.
    /// </summary>
    Task<int> RecordForgottenAsync(
        Guid userId,
        IReadOnlyCollection<string> emails,
        Instant anonymizedAt,
        CancellationToken ct = default);

    /// <summary>True if <paramref name="email"/> hashes to a known forgotten row.</summary>
    Task<bool> IsForgottenAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Returns the subset of <paramref name="emails"/> whose hash is in the
    /// skip-list. Batch variant for the import classifier.
    /// </summary>
    Task<IReadOnlySet<string>> GetForgottenAsync(
        IReadOnlyCollection<string> emails, CancellationToken ct = default);

    /// <summary>Skip-list size; surfaced on the dashboard.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
