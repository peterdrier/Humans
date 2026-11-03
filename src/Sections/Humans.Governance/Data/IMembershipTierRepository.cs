using Humans.Base.Interfaces.Repositories;
using Humans.Governance.Domain;

namespace Humans.Governance.Data;

internal interface IMembershipTierRepository : IRepository
{
    Task<MembershipTierRecord?> GetAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<MembershipTierRecord>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Inserts or overwrites the member's row.</summary>
    Task UpsertAsync(MembershipTierRecord values, CancellationToken ct = default);

    /// <summary>Account-merge fold: moves the source row to the target when the target has
    /// none, otherwise deletes it.</summary>
    Task ReassignAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default);
}
