using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>forgotten_emails</c> table. Append-only.
/// The only code path that touches this DbSet.
/// </summary>
public interface IForgottenEmailRepository : IRepository
{
    /// <summary>
    /// Returns true if any row exists with this email hash. Used by Mailer
    /// import to skip GDPR-anonymized addresses.
    /// </summary>
    Task<bool> ExistsByHashAsync(string emailHash, CancellationToken ct = default);

    /// <summary>
    /// Returns the subset of <paramref name="hashes"/> that match any
    /// forgotten-email row. Batch variant for the import classifier.
    /// </summary>
    Task<IReadOnlySet<string>> GetExistingHashesAsync(
        IReadOnlyCollection<string> hashes, CancellationToken ct = default);

    /// <summary>
    /// Inserts forgotten-email rows. Idempotent on (UserId, EmailHash) — rows
    /// whose pair already exists are silently skipped. Returns the count of
    /// rows actually inserted.
    /// </summary>
    Task<int> AddManyAsync(
        Guid userId,
        IReadOnlyCollection<string> emailHashes,
        Instant anonymizedAt,
        CancellationToken ct = default);

    /// <summary>Total row count (used by the dashboard).</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
