using System.Globalization;
using Humans.Base.Helpers;
using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
using Humans.Expenses.Services;
using Humans.Finance.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Expenses.Models;

internal sealed class ExpensePageBuilder(
    IExpenseReportManagement service,
    IBudgetService budgetService,
    IFinanceService finance,
    IPreapprovalService preapprovals,
    IClock clock,
    IStringLocalizer<ExpensesResource> localizer)
{
    /// <summary>Claim deadlines are Europe/Madrid dates.</summary>
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    internal LocalDate Today => clock.GetCurrentInstant().InZone(MadridZone).Date;

    public async Task<ExpensesIndexViewModel> BuildIndexAsync(UserInfo viewer, CancellationToken ct = default)
    {
        var userId = viewer.Id;
        // Newest first across every merged account id, not per id.
        var reports = (await service.GetForSubmitterAsync(userId, ct))
            .OrderByDescending(r => r.CreatedAt).ToList();
        var activeYear = await budgetService.GetActiveYearAsync(ct);

        var bookingLabels = await ResolveBookingLabelsAsync(reports, ct);

        var coordinatorQueue = await service.GetCoordinatorQueueAsync(userId, ct);
        var coordinatorTeamIds = await budgetService.GetEffectiveCoordinatorTeamIdsAsync(userId, ct);
        var openGrants = (await preapprovals.ListClaimableAsync(viewer.AllUserIds, ct))
            .OrderBy(g => g.ClaimBy).ThenBy(g => g.ProgramName, StringComparer.OrdinalIgnoreCase).ToList();

        // Own account only. GetCreditorLedgerAsync returns null both for "not bound" and "bound, nothing cached
        // yet", so the binding is read separately: telling a bound member to get bound is worse than silence.
        CreditorLedger? accountLedger = null;
        var binding = await finance.GetCreditorContactByUserAsync(userId, ct);
        var boundAccountNum = binding?.SupplierAccountNum;
        if (boundAccountNum is { } accNum)
        {
            var led = await finance.GetCreditorLedgerAsync(accNum, ct);
            if (led is not null)
                accountLedger = led with
                {
                    Lines = led.Lines
                        .OrderByDescending(l => l.Date)
                        .ThenByDescending(l => l.EntryNumber)
                        .ThenBy(l => l.Line)
                        .ToList()
                };
        }

        var model = new ExpensesIndexViewModel
        {
            Reports = reports,
            HasActiveYear = activeYear is not null,
            HasIban = (await GetIbanViewAsync(userId, ct)).HasIban,
            BookingLabels = bookingLabels,
            OpenGrants = openGrants,
            Today = Today,
            AccountLedger = accountLedger,
            BoundAccountNum = boundAccountNum,
            // A claim waiting on this viewer's sign-off always earns the link.
            IsCoordinator = coordinatorTeamIds.Count > 0 || coordinatorQueue.Count > 0,
            CoordinatorQueueCount = coordinatorQueue.Count,
        };
        return model;
    }

    public async Task<ExpenseReviewViewModel> BuildReviewAsync(
        Guid userId, bool isFinanceAdmin, CancellationToken ct = default)
    {
        var reports = (await service.GetReviewQueueAsync(userId, isFinanceAdmin, ct))
            .OrderByDescending(r => r.SubmittedAt ?? r.CreatedAt)
            .ToList();
        return new ExpenseReviewViewModel
        {
            Reports = reports,
            BookingLabels = await ResolveBookingLabelsAsync(reports, ct),
            Grants = await ResolveGrantsAsync(reports, ct),
            FailedHoldedPushReportIds = (await service.GetFailedHoldedPushReportIdsAsync(
                isFinanceAdmin, ct)).ToHashSet(),
            IsFinanceAdmin = isFinanceAdmin,
        };
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveBookingLabelsAsync(
        IReadOnlyList<ExpenseReportDto> reports, CancellationToken ct)
    {
        var labels = new Dictionary<Guid, string>();
        if (reports.Count == 0) return labels;

        Dictionary<int, string>? accountLabels = null;
        Dictionary<Guid, string>? unpinnedCategoryLabels = null;
        var categoryLabelsByYear = new Dictionary<Guid, Dictionary<Guid, string>>();
        foreach (var report in reports)
        {
            if (report.HoldedAccountNumber != 0)
            {
                accountLabels ??= (await finance.ListExpenseAccountsAsync(activeOnly: false, ct))
                    .GroupBy(a => a.AccountNum)
                    .ToDictionary(g => g.Key, g => g.First().Label);
                labels[report.Id] = accountLabels.TryGetValue(report.HoldedAccountNumber, out var accountLabel)
                    ? accountLabel
                    : report.HoldedAccountNumber.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                var categoryLabels = report.BudgetYearId is { } yearId
                    ? categoryLabelsByYear.GetValueOrDefault(yearId)
                    : unpinnedCategoryLabels;
                if (categoryLabels is null)
                {
                    var year = report.BudgetYearId is { } bookedYearId
                        ? await budgetService.GetYearByIdAsync(bookedYearId, ct)
                        : await budgetService.GetActiveYearAsync(ct);
                    categoryLabels = year?.Groups
                        .SelectMany(g => g.Categories.Select(c => (c.Id, Display: $"{g.Name} / {c.Name}")))
                        .ToDictionary(x => x.Id, x => x.Display)
                        ?? [];
                    if (report.BudgetYearId is { } loadedYearId)
                        categoryLabelsByYear[loadedYearId] = categoryLabels;
                    else
                        unpinnedCategoryLabels = categoryLabels;
                }
                labels[report.Id] = report.BudgetCategoryId is { } categoryId
                                    && categoryLabels.TryGetValue(categoryId, out var categoryLabel)
                    ? categoryLabel
                    : localizer["Expenses_Detail_UnknownCategory"].Value;
            }
        }
        return labels;
    }

    private async Task<IReadOnlyDictionary<Guid, GrantOption>> ResolveGrantsAsync(
        IReadOnlyList<ExpenseReportDto> reports, CancellationToken ct)
    {
        var all = (await preapprovals.GetGrantsAsync(ct)).ToDictionary(g => g.Id);
        var grants = new Dictionary<Guid, GrantOption>();
        foreach (var report in reports)
            if (report.PreapprovalGrantId is { } grantId && all.TryGetValue(grantId, out var grant))
                grants[report.Id] = grant;
        return grants;
    }

    public async Task<(bool HasIban, string? MaskedIban)> GetIbanViewAsync(Guid userId, CancellationToken ct = default)
    {
        var iban = await finance.GetIbanAsync(userId, ct);
        var hasIban = !string.IsNullOrEmpty(iban);
        return (hasIban, hasIban ? IbanFormatter.Mask(iban!) : null);
    }
}
