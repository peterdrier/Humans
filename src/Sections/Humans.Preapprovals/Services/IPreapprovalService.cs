using Humans.Preapprovals.Domain;

namespace Humans.Preapprovals.Services;

/// <summary>
/// The grant reads an expense form needs: a member's claimable grants and one grant by id.
/// Nothing here changes state. Internal — no other section consumes Preapprovals yet; when
/// Expenses does, this is the face that becomes public in a Contracts folder.
/// </summary>
internal interface IPreapprovalService
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
