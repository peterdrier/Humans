using Humans.Base.Authorization;

namespace Humans.Permissions.Contracts;

/// <summary>
/// A section that knows what people hold on owners: Roles, and later Tasks. Implement it on the section's caching
/// singleton, under the same DI rule as <see cref="IOwnerKind"/>.
/// </summary>
public interface IPermissionSource
{
    Task<IReadOnlyList<Holding>> GetHoldingsAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>What one person holds on one owner.</summary>
/// <param name="Owner">Where the holding is.</param>
/// <param name="IsCoordinator">The owner's coordinator role: every permission that applies to the owner's kind.</param>
/// <param name="Keys">Permission keys held when not the coordinator. Keys no section declares are ignored.</param>
/// <param name="Reason">Why, for the permissions page: "Cantina coordinator".</param>
public sealed record Holding(Owner Owner, bool IsCoordinator, IReadOnlyList<string> Keys, string Reason);
