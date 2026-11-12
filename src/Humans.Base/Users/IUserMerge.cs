using NodaTime;

namespace Humans.Users.Contracts;

/// <summary>
/// The merge runs these as independently committing steps with no wrapping transaction, so implementations must be
/// idempotent: re-running after a partial failure must be a safe no-op. An implementation that caches the rows it
/// moves evicts that cache itself once its step returns.
/// </summary>
public interface IUserMerge
{
    /// <summary>
    /// Moves this section's rows from <paramref name="mergedFromUserId"/> (being tombstoned) to
    /// <paramref name="mergedToUserId"/>, resolving conflicts by the section's own rules and stamping
    /// <paramref name="now"/> as <c>UpdatedAt</c> where there is one.
    /// </summary>
    Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct);
}
