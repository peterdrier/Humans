using Humans.Base.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>
/// A section that keeps state keyed by one of a human's addresses. Users calls it after an address leaves the
/// account: a delete, a provider unlink, an OAuth tag-move or rename, or a cross-user displacement.
/// </summary>
public interface IUserEmailRemovalListener : IFanout
{
    /// <summary>Runs after the removal commits, so it must be idempotent; a no-op for an address it does not hold.</summary>
    Task AddressRemovedAsync(Guid userId, string email, CancellationToken ct = default);
}
