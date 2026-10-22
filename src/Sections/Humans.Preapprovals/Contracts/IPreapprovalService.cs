using Humans.Base.Interfaces;

namespace Humans.Preapprovals.Contracts;

public interface IPreapprovalService : IApplicationService
{
    /// <summary>
    /// Open and the claim deadline not yet passed (Europe/Madrid). Every id the person has ever held is searched, so a
    /// grant issued under a since-merged id is still found.
    /// </summary>
    Task<IReadOnlyList<GrantOption>> ListClaimableForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>Any state; null when it does not exist.</summary>
    Task<GrantOption?> GetGrantAsync(Guid grantId, CancellationToken ct);

    /// <summary>
    /// Every grant, in any state, of the programs the person is a named manager of under any id they have held. Admin,
    /// Board and FinanceAdmin reach every program by role, which this does not count.
    /// </summary>
    Task<IReadOnlySet<Guid>> ListManagedGrantIdsAsync(Guid userId, CancellationToken ct);
}
