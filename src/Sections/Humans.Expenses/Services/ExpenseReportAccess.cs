using Humans.Finance.Contracts;
using Humans.Expenses.Services.Dtos;
using System.Globalization;
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
    IPreapprovalService preapprovals,
    IFinanceService finance) : IApplicationService
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
        var managedGrantIds = await preapprovals.ListManagedGrantIdsAsync(viewer.AllUserIds, ct);
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
        var managedGrantIds = await preapprovals.ListManagedGrantIdsAsync(coordinator.AllUserIds, ct);

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
    /// Checks the command's decision window and requires a top-level line. The caller authorizes the reviewer.
    /// </summary>
    internal async Task<(ExpenseReportDto? Report, ExpenseLineDto? Line, ExpenseMutationResult? Refusal)>
        GetDeclinableLineAsync(
            Guid reportId, Guid lineId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses, CancellationToken ct)
    {
        const string cannot = "Could not change the line. The report may no longer be awaiting a decision.";
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return (null, null, ExpenseMutationResult.OperatorFailure(cannot));

        if (!allowedStatuses.Contains(report.Status))
            return (null, null, ExpenseMutationResult.OperatorFailure(cannot));

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

    private async Task<IReadOnlyList<Guid>> AllIdsAsync(Guid userId, CancellationToken ct) =>
        (await userService.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];

    /// <summary>Submitted, not yet approved: has a payee snapshot and a booked year, but is not final.</summary>
    internal static bool IsPendingApproval(ExpenseReportStatus status) =>
        status is ExpenseReportStatus.Submitted or ExpenseReportStatus.CoordinatorEndorsed;

    internal async Task<(decimal? Amount, ExpenseMutationResult? Refusal)> ResolveReviewCapAsync(
        Guid? grantId, decimal? requestedAmount, CancellationToken ct)
    {
        if (requestedAmount is { } amount && decimal.Round(amount, 2) != amount)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_AmountPrecision"));

        if (grantId is not { } id) return (requestedAmount, null);

        var grant = await preapprovals.GetGrantAsync(id, ct);
        if (grant is null)
            return (null, ExpenseMutationResult.OperatorFailure("The report's pre-approval no longer exists; reject the report."));
        if (requestedAmount > grant.MaxAmount)
            return (null, ExpenseMutationResult.OperatorFailure(
                $"The cap cannot exceed the pre-approved {grant.MaxAmount.ToString("0.00", CultureInfo.InvariantCulture)} EUR."));

        return (requestedAmount ?? grant.MaxAmount, null);
    }

    /// <summary>
    /// The grant's account, with no category or year, which routes the report past endorsement. The grant must be
    /// claimable by the submitter today; the one a draft already holds (<paramref name="heldGrantId"/>) is always kept,
    /// so a rejected report can be corrected after the deadline it met.
    /// </summary>
    internal async Task<(ExpenseBooking? Booking, ExpenseMutationResult? Refusal)> ResolveGrantBookingAsync(
        Guid grantId, Guid submitterUserId, Guid? heldGrantId, CancellationToken ct)
    {
        var grant = grantId == heldGrantId
            ? await preapprovals.GetGrantAsync(grantId, ct)
            : (await preapprovals.ListClaimableAsync(await AllIdsAsync(submitterUserId, ct), ct)).FirstOrDefault(g => g.Id == grantId);
        if (grant is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalNotClaimable"));
        return (new ExpenseBooking(grant.HoldedAccountNumber, grant.HoldedAccountId, null, null), null);
    }

    /// <summary>
    /// Resolves what a report books to from the account the submitter picked. A budget-category
    /// account also carries the category it maps to and that category's year; any other account
    /// carries neither, which is what routes the report past coordinator endorsement. A draft takes
    /// the active year's categories; a report past submit (<paramref name="pinnedYearId"/> set)
    /// accepts only its own year's, so a header edit never moves its accounting year.
    /// </summary>
    internal async Task<(ExpenseBooking? Booking, ExpenseMutationResult? Refusal)> ResolveBookingAsync(
        int holdedAccountNumber, Guid? pinnedYearId, CancellationToken ct)
    {
        var account = (await finance.ListExpenseAccountsAsync(activeOnly: true, ct))
            .FirstOrDefault(a => a.AccountNum == holdedAccountNumber);
        if (account is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_AccountNotFound"));
        if (!account.IsBudgetCategory)
            return (new ExpenseBooking(account.AccountNum, account.AccountId, null, null), null);

        // The option list said this account is a budget category, so the map row exists; the
        // category behind it may still be gone or belong to another year.
        var mapRow = (await finance.GetCategoryMapAsync(ct))
            .FirstOrDefault(m => m.HoldedAccountNumber == holdedAccountNumber);
        if (mapRow is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryNotFound"));

        if (pinnedYearId is { } pinned)
        {
            var snapshot = await budgetService.GetCategoryByIdAsync(mapRow.BudgetCategoryId, ct);
            if (snapshot is null)
                return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryNotFound"));
            if (snapshot.BudgetGroup?.BudgetYearId != pinned)
                return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryDifferentBudgetYear"));
            return (new ExpenseBooking(account.AccountNum, account.AccountId, snapshot.Id, pinned), null);
        }

        var activeYear = await budgetService.GetActiveYearAsync(ct)
            ?? throw new InvalidOperationException("No active budget year.");
        var inActiveYear = activeYear.Groups.SelectMany(g => g.Categories).Any(c => c.Id == mapRow.BudgetCategoryId);
        if (!inActiveYear)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryNotFound"));
        return (new ExpenseBooking(account.AccountNum, account.AccountId, mapRow.BudgetCategoryId, activeYear.Id), null);
    }

}
