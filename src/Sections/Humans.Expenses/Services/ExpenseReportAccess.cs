using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Preapprovals.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;

namespace Humans.Expenses.Services;

internal sealed class ExpenseReportAccess(
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

    /// <summary>
    /// The service half of the DeclineLine gate: the report must still await a decision (a first reviewer only while
    /// Submitted, a finance admin through CoordinatorEndorsed), and the line must be top-level. Proof rows are never
    /// declinable.
    /// </summary>
    internal async Task<(ExpenseReportDto? Report, ExpenseLineDto? Line, ExpenseMutationResult? Refusal)>
        GetDeclinableLineAsync(
            Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId, CancellationToken ct)
    {
        const string cannot = "Could not change the line. The report may no longer be awaiting a decision.";
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return (null, null, ExpenseMutationResult.OperatorFailure(cannot));

        var open = actorIsFinanceAdmin
            ? report.Status is ExpenseReportStatus.Submitted or ExpenseReportStatus.CoordinatorEndorsed
            : report.Status is ExpenseReportStatus.Submitted;
        if (!open) return (null, null, ExpenseMutationResult.OperatorFailure(cannot));

        if (!actorIsFinanceAdmin
            && await GetFirstReviewerRefusalAsync(report, actorUserId, ct) is { } reviewerRefusal)
            return (null, null, reviewerRefusal);

        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return (null, null, ExpenseMutationResult.Failure("Expenses_Validation_LineNotOnReport"));
        if (line.ParentLineId is not null)
            return (null, null, ExpenseMutationResult.OperatorFailure("Proof rows cannot be declined; decline the invoice line instead."));
        return (report, line, null);
    }

    /// <summary>
    /// The service half of the edit gate. A member edits only their own Draft. A finance admin edits on their behalf,
    /// so ownership is waived and the window is Draft, Submitted and CoordinatorEndorsed.
    /// </summary>
    internal async Task<(ExpenseReportDto? Report, ExpenseMutationResult? Refusal)> GetEditableReportAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, CancellationToken ct)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Iban_ReportNotFound"));
        if (!actorIsFinanceAdmin && !await IsSubmitterAsync(report, actorUserId, ct))
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_OnlySubmitterCanEdit"));

        var editable = actorIsFinanceAdmin
            ? report.Status is ExpenseReportStatus.Draft or ExpenseReportStatus.Submitted or ExpenseReportStatus.CoordinatorEndorsed
            : report.Status is ExpenseReportStatus.Draft;
        return editable
            ? (report, null)
            : (null, ExpenseMutationResult.Failure("Expenses_Validation_ReportCannotBeEditedInStatus", report.Status));
    }

    internal async Task<bool> IsSubmitterAsync(ExpenseReportDto report, Guid actorUserId, CancellationToken ct) =>
        report.SubmitterUserId == actorUserId
        || (await userService.GetUserInfoAsync(actorUserId, ct))?.AllUserIds.Contains(report.SubmitterUserId) == true;

    /// <summary>
    /// A pre-approved report has no category; its first reviewer is a manager of the grant's program, the person who
    /// already decided it.
    /// </summary>
    internal async Task<ExpenseMutationResult?> GetFirstReviewerRefusalAsync(
        ExpenseReportDto report, Guid actorUserId, CancellationToken ct)
    {
        if (report.PreapprovalGrantId is { } grantId)
            return (await preapprovals.ListManagedGrantIdsAsync(actorUserId, ct)).Contains(grantId)
                ? null
                : ExpenseMutationResult.OperatorFailure("Actor is not a manager of the report's pre-approval program.");

        var categoryId = report.BudgetCategoryId;
        if (categoryId is null)
            return ExpenseMutationResult.OperatorFailure("Report is not booked to a budget category; there is no coordinator to endorse it.");
        var category = await budgetService.GetCategoryByIdAsync(categoryId.Value, ct);
        if (category is null)
            throw new InvalidOperationException("Budget category not found.");
        if (!category.TeamId.HasValue)
            return ExpenseMutationResult.OperatorFailure("Category has no owning team; coordinator endorsement is not valid.");
        var isCoordinator = await teamService.IsUserCoordinatorOfTeamAsync(
            category.TeamId.Value, actorUserId, ct);
        if (!isCoordinator)
            return ExpenseMutationResult.OperatorFailure("Actor is not a coordinator of the category's team.");
        return null;
    }
}
