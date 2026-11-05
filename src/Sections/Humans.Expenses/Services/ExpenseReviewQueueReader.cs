using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Preapprovals.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;

namespace Humans.Expenses.Services;

internal sealed class ExpenseReviewQueueReader(
    IExpenseRepository repo,
    IUserService userService,
    IBudgetService budgetService,
    ITeamService teamService,
    IPreapprovalService preapprovals) : IApplicationService
{
    public async Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(
        Guid viewerUserId, bool isFinanceAdmin, CancellationToken ct = default)
    {
        var queue = await repo.GetForReviewQueueAsync(ct);
        if (isFinanceAdmin) return queue;

        // Filtering the whole queue in memory beats a per-audience query at this scale.
        // Drafts and withdrawals are excluded upstream for everyone.
        var viewer = await userService.GetUserInfoAsync(viewerUserId, ct);
        if (viewer is null) return [];
        var categoryIds = await GetCoordinatorCategoryIdsAsync(viewer.Id, ct);
        var managedGrantIds = await preapprovals.ListManagedGrantIdsAsync(viewer.Id, ct);
        var ownIds = viewer.AllUserIds;
        return queue
            .Where(r => ownIds.Contains(r.SubmitterUserId)
                        || (r.BudgetCategoryId is { } categoryId && categoryIds.Contains(categoryId))
                        || (r.PreapprovalGrantId is { } grantId && managedGrantIds.Contains(grantId)))
            .ToList();
    }

    public async Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default)
    {
        var coordinator = await userService.GetUserInfoAsync(coordinatorUserId, ct);
        if (coordinator is null) return [];
        var categoryIds = await GetCoordinatorCategoryIdsAsync(coordinator.Id, ct);
        var managedGrantIds = await preapprovals.ListManagedGrantIdsAsync(coordinator.Id, ct);

        var queue = categoryIds.Count == 0
            ? []
            : (await repo.GetByCategoryIdsAndStatusAsync(categoryIds, ExpenseReportStatus.Submitted, ct)).ToList();
        if (managedGrantIds.Count > 0)
            queue.AddRange((await repo.GetForReviewQueueAsync(ct)).Where(r =>
                r.Status == ExpenseReportStatus.Submitted
                && r.PreapprovalGrantId is { } grantId && managedGrantIds.Contains(grantId)));
        return queue;
    }

    private async Task<IReadOnlyList<Guid>> GetCoordinatorCategoryIdsAsync(
        Guid coordinatorUserId, CancellationToken ct)
    {
        var year = await budgetService.GetActiveYearAsync(ct);
        if (year is null) return [];

        // The same rule that grants endorse: whoever can sign a claim off sees it.
        var categories = year.Groups.SelectMany(g => g.Categories).Where(c => c.TeamId.HasValue).ToList();
        var coordinatedTeamIds = new HashSet<Guid>();
        foreach (var teamId in categories.Select(c => c.TeamId!.Value).Distinct())
        {
            if (await teamService.IsUserCoordinatorOfTeamAsync(teamId, coordinatorUserId, ct))
                coordinatedTeamIds.Add(teamId);
        }

        return categories
            .Where(c => coordinatedTeamIds.Contains(c.TeamId!.Value))
            .Select(c => c.Id)
            .ToList();
    }

}
