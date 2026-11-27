using Humans.Preapprovals.Contracts;

namespace Humans.Expenses.Services;

/// <summary>
/// The three questions Expenses asks of pre-approvals, answered from the one list Preapprovals publishes.
/// <c>personIds</c> is every id the person has held, so a grant under a since-merged id is still theirs.
/// </summary>
internal static class PreapprovalGrantQueries
{
    internal static async Task<GrantOption?> GetGrantAsync(
        IPreapprovalService preapprovals, Guid grantId, CancellationToken ct) =>
        (await preapprovals.GetGrantsAsync(ct)).FirstOrDefault(g => g.Id == grantId);

    /// <summary>
    /// The validation key saying why a grant cannot be filed against now, for one that is not the person's
    /// claimable or held grant. The deadline key takes the claim-by date.
    /// </summary>
    internal static string UnavailableKey(GrantOption? grant) => grant?.Status switch
    {
        null => "Expenses_Validation_PreapprovalNotFound",
        GrantStatus.Revoked => "Expenses_Validation_PreapprovalRevoked",
        GrantStatus.Claimed => "Expenses_Validation_PreapprovalClaimedElsewhere",
        GrantStatus.Consumed => "Expenses_Validation_PreapprovalUsed",
        GrantStatus.Applied => "Expenses_Validation_PreapprovalNotGranted",
        GrantStatus.Declined => "Expenses_Validation_PreapprovalDeclined",
        _ => grant.ClaimableToday ? "Expenses_Validation_PreapprovalNotHeld" : "Expenses_Validation_PreapprovalDeadlinePassed"
    };

    /// <summary>The grants this person may file an expense against today.</summary>
    internal static async Task<IReadOnlyList<GrantOption>> ListClaimableAsync(
        IPreapprovalService preapprovals, IReadOnlyCollection<Guid> personIds, CancellationToken ct) =>
        (await preapprovals.GetGrantsAsync(ct))
            .Where(g => g.ClaimableToday && personIds.Contains(g.HolderUserId))
            .ToList();

    /// <summary>
    /// Every grant, in any state, of the programs this person is a named manager of. Admin, Board and FinanceAdmin
    /// reach every program by role, which this does not count.
    /// </summary>
    internal static async Task<IReadOnlySet<Guid>> ListManagedGrantIdsAsync(
        IPreapprovalService preapprovals, IReadOnlyCollection<Guid> personIds, CancellationToken ct) =>
        (await preapprovals.GetGrantsAsync(ct))
            .Where(g => g.ManagerUserIds.Any(personIds.Contains))
            .Select(g => g.Id)
            .ToHashSet();
}
