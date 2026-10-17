namespace Humans.Preapprovals.Contracts;

/// <summary>
/// The cross-section read surface of Preapprovals. Expenses lists a member's claimable grants
/// on its forms and reads one back by id; nothing here changes state.
/// </summary>
public interface IPreapprovalServiceRead
{
    /// <summary>
    /// The grants <paramref name="userId"/> may file against today: <see cref="GrantStatus.Open"/>
    /// and the program's claim deadline not yet passed (Europe/Madrid). Every id the person has
    /// ever held is searched, so a grant issued under a since-merged id is still found.
    /// </summary>
    Task<IReadOnlyList<GrantOption>> ListClaimableForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>One grant in any state, or null when it does not exist.</summary>
    Task<GrantOption?> GetGrantAsync(Guid grantId, CancellationToken ct);
}
