using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Workgroups.Data;
using Humans.Workgroups.Domain;
using NodaTime;
using static Humans.Workgroups.Services.WorkgroupAccess;
using static Humans.Workgroups.Services.WorkgroupJournal;

namespace Humans.Workgroups.Services;

internal sealed class WorkgroupBudgets(
    IWorkgroupRepository repository,
    WorkgroupAccess access,
    WorkgroupJournal journal,
    IFinanceWrite finance,
    IHoldedService holded,
    IClock clock,
    ILogger<WorkgroupService> logger) : IApplicationService
{
    private const decimal MaxBudgetAmount = 9_999_999_999_999_999.99m;

    internal async Task<WorkgroupMutationResult<ExpenseAccountRef?>> SetBudgetAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (ValidateBudgetAmount(save.Amount) is { } budgetRefusal)
            return new(Refusal: budgetRefusal);

        var workgroupResult = await access.RequireAsync(workgroupId, ct);
        if (workgroupResult.Refusal is { } workgroupRefusal) return new(Refusal: workgroupRefusal);
        var workgroup = workgroupResult.Value!;
        if (RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred,
            WorkgroupStatus.Active, WorkgroupStatus.Dormant) is { } statusRefusal) return new(Refusal: statusRefusal);

        // Finance first, before any write: a failed create or unknown account leaves the row as it was.
        ExpenseAccountRef? account = null;
        var needsAccount = save.Amount is not null
            && (workgroup.HoldedAccountNumber is null
                || (save.ExistingAccountNum is { } wanted && wanted != workgroup.HoldedAccountNumber));
        if (needsAccount)
        {
            // Account resolution can create remote state; finish its local binding and record.
            ct = CancellationToken.None;
            var accountResult = await ResolveBudgetAccountAsync(workgroup, save.ExistingAccountNum, ct);
            if (accountResult.Refusal is { } accountRefusal) return new(Refusal: accountRefusal);
            account = accountResult.Value!;
        }

        var now = clock.GetCurrentInstant();
        workgroup.BudgetAmount = save.Amount;
        if (account is not null)
        {
            workgroup.HoldedAccountNumber = account.AccountNum;
            workgroup.HoldedAccountId = account.AccountId;
        }
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct,
            [SystemEntry(workgroup.Id, WorkgroupLogKind.BudgetSet, now, BudgetLogBody(workgroup),
                authorUserId: actorUserId)]);
        await journal.AuditAsync(WorkgroupsAuditActions.WorkgroupBudgetSet, workgroup, BudgetAuditSummary(workgroup), actorUserId);
        return new(account);
    }

    internal async Task<IReadOnlyList<HoldedChartAccountInfo>> ListExpenseAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            return (await holded.GetChartAsync(ct))
                .Where(a => a.Number is >= 60000000 and <= 69999999 && !a.Archived)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Holded mirror unavailable; expense accounts could not be loaded");
            throw;
        }
    }

    /// <summary>Finance faults log and return retry guidance; cancellation propagates.</summary>
    internal async Task<WorkgroupMutationResult<ExpenseAccountRef>> ResolveBudgetAccountAsync(
        Workgroup workgroup, int? existingAccountNum, CancellationToken ct)
    {
        try
        {
            return new(await finance.CreateOrLinkExpenseAccountAsync(
                $"Workgroups / {workgroup.Name}", existingAccountNum, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Finance could not resolve a Holded account for workgroup {WorkgroupId}", workgroup.Id);
            return new(Refusal: new(WorkgroupErrorKeys.BudgetAccountFailed));
        }
    }

    internal static WorkgroupRefusal? ValidateBudgetAmount(decimal? amount)
    {
        if (amount is < 0) return new(WorkgroupErrorKeys.BudgetNegative);
        if (amount is > MaxBudgetAmount)
            return new("The budget amount is too large.") { IsOperatorMessage = true };
        if (amount is { } value && decimal.Round(value, 2) != value)
            return new("Budget amounts must have no more than two decimal places.") { IsOperatorMessage = true };
        return null;
    }

    // Read in all supported cultures under the localized "Budget set" badge, so it carries figures only, never English prose.
    internal static string BudgetLogBody(Workgroup w) => w.BudgetAmount is { } amount
        ? $"{amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR · {w.HoldedAccountNumber}"
        : "—";

    internal static string BudgetAuditSummary(Workgroup w) => w.BudgetAmount is { } amount
        ? $"Budget set to {amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR, account {w.HoldedAccountNumber}"
        : "Budget cleared";

    /// <summary>
    /// Failures are logged, never surfaced: the lifecycle step is the Board's decision and doesn't wait on Holded.
    /// </summary>
    internal async Task SetAccountActiveAsync(Workgroup w, bool isActive, CancellationToken ct)
    {
        if (w.HoldedAccountNumber is not { } accountNum) return;
        try
        {
            await finance.SetExpenseAccountActiveAsync(accountNum, isActive, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not set Holded account {AccountNum} active={IsActive} for workgroup {WorkgroupId}",
                accountNum, isActive, w.Id);
        }
    }

}
