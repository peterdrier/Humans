using Humans.Preapprovals.Contracts;

namespace Humans.Expenses.Services;

/// <summary>
/// The three questions Expenses asks of pre-approvals, answered from the one list Preapprovals publishes.
/// <c>personIds</c> is every id the person has held, so a grant under a since-merged id is still theirs.
/// </summary>
internal static class PreapprovalGrantQueries
{
    internal static async Task<GrantOption?> GetGrantAsync(
        this IPreapprovalService preapprovals, Guid grantId, CancellationToken ct) =>
        (await preapprovals.GetGrantsAsync(ct)).FirstOrDefault(g => g.Id == grantId);

    /// <summary>The grants this person may file an expense against today.</summary>
    internal static async Task<IReadOnlyList<GrantOption>> ListClaimableAsync(
        this IPreapprovalService preapprovals, IReadOnlyCollection<Guid> personIds, CancellationToken ct) =>
        (await preapprovals.GetGrantsAsync(ct))
            .Where(g => g.ClaimableToday && personIds.Contains(g.HolderUserId))
            .ToList();

    /// <summary>
    /// Every grant, in any state, of the programs this person is a named manager of. Admin, Board and FinanceAdmin
    /// reach every program by role, which this does not count.
    /// </summary>
    internal static async Task<IReadOnlySet<Guid>> ListManagedGrantIdsAsync(
        this IPreapprovalService preapprovals, IReadOnlyCollection<Guid> personIds, CancellationToken ct) =>
        (await preapprovals.GetGrantsAsync(ct))
            .Where(g => g.ManagerUserIds.Any(personIds.Contains))
            .Select(g => g.Id)
            .ToHashSet();
}
