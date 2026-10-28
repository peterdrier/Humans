using Humans.Base.Extensions;
using Humans.Budget.Contracts;
using Humans.Budget.Services;
using Humans.Finance.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Controllers;
using Humans.Budget.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NodaTime;

using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.Budget.Controllers;

/// <summary>
/// Shares the <c>/Finance</c> route prefix with Finance's <c>FinanceController</c>; the action templates are disjoint.
/// </summary>
[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
[Route("Finance")]
internal sealed class BudgetAdminController(
    IBudgetManagement budgetService,
    ITeamService teamService,
    TicketingBudgetService ticketingBudgetService,
    IClock clock,
    IUserService userService,
    IHoldedFinanceService holdedFinance,
    ILogger<BudgetAdminController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var activeYear = await budgetService.GetActiveYearAsync(ct);
            if (activeYear is null)
            {
                ViewBag.Years = await budgetService.GetAllYearsAsync(ct: ct);
                return View("NoActiveYear");
            }

            var allYears = await budgetService.GetAllYearsAsync(ct: ct);
            var model = await BuildFinanceOverviewAsync(activeYear, allYears, ct);
            return View("YearDetail", model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading finance index");
            SetError("Failed to load budget data.");
            return View("NoActiveYear");
        }
    }

    [HttpGet("Years/{id:guid}")]
    public async Task<IActionResult> YearDetail(Guid id)
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var year = await budgetService.GetYearByIdAsync(id, ct);
            if (year is null) return NotFound();

            var allYears = await budgetService.GetAllYearsAsync(ct: ct);
            var model = await BuildFinanceOverviewAsync(year, allYears, ct);
            return View(model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading budget year {YearId}", id);
            SetError("Failed to load budget year.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("Categories/{id:guid}")]
    public async Task<IActionResult> CategoryDetail(Guid id)
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var category = await budgetService.GetCategoryByIdAsync(id, ct);
            if (category is null) return NotFound();

            ViewBag.Teams = (await teamService.GetTeamsAsync(ct)).Values
                .Where(t => t.IsActive)
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();

            return View(category);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading budget category {CategoryId}", id);
            SetError("Failed to load category.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("AuditLog/{yearId:guid?}")]
    public async Task<IActionResult> AuditLog(Guid? yearId)
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            if (!yearId.HasValue)
            {
                var active = await budgetService.GetActiveYearAsync(ct);
                yearId = active?.Id;
            }

            var entries = await budgetService.GetAuditLogAsync(yearId, ct);
            ViewBag.YearId = yearId;
            ViewBag.Years = await budgetService.GetAllYearsAsync(includeArchived: true, ct);
            return View(entries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading budget audit log");
            SetError("Failed to load audit log.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("Admin")]
    public async Task<IActionResult> Admin()
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var years = await budgetService.GetAllYearsAsync(includeArchived: true, ct);
            return View(years);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading finance admin");
            SetError("Failed to load finance admin.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("Years/{id:guid}/SyncDepartments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncDepartments(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var count = await budgetService.SyncDepartmentsAsync(id, user.Id);
            if (count > 0)
                SetSuccess($"Synced {count} new department(s) into budget.");
            else
                SetInfo("All budget-enabled teams are already in the Departments group.");
            return RedirectToAction(nameof(YearDetail), new { id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing departments for year {YearId}", id);
            SetError($"Failed to sync departments: {ex.Message}");
            return RedirectToAction(nameof(YearDetail), new { id });
        }
    }

    [HttpPost("Years/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateYear(string year, string name)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (string.IsNullOrWhiteSpace(year) || string.IsNullOrWhiteSpace(name))
        {
            SetError("Year identifier and name are required.");
            return RedirectToAction(nameof(Admin));
        }

        try
        {
            await budgetService.CreateYearAsync(year, name, user.Id);
            SetSuccess($"Budget year '{name}' created.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating budget year {Year}", year);
            SetError($"Failed to create budget year: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Years/{id:guid}/UpdateStatus")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateYearStatus(Guid id, [BindRequired] BudgetYearStatus status)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed budget year status for {YearId}", id);
            return BadRequest(ModelState);
        }

        var result = await budgetService.UpdateYearStatusAsync(id, status, user.Id);
        if (result.Succeeded)
            SetSuccess($"Budget year status updated to {status}.");
        else
        {
            logger.LogWarning("Budget year status change refused for {YearId}, actor {UserId}: {Reason}", id, user.Id, result.Refusal);
            SetError($"Failed to update status: {result.Refusal}");
        }
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Years/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateYear(Guid id, string year, string name, string? vendorEventId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var result = await budgetService.UpdateYearAsync(id, year, name, vendorEventId, user.Id);
        if (result.Succeeded)
            SetSuccess("Budget year updated.");
        else
        {
            logger.LogWarning("Budget year update refused for {YearId}, actor {UserId}: {Reason}", id, user.Id, result.Refusal);
            SetError($"Failed to update budget year: {result.Refusal}");
        }
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Years/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteYear(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var result = await budgetService.DeleteYearAsync(id, user.Id);
        if (result.Succeeded)
            SetSuccess("Budget year deleted.");
        else
        {
            logger.LogWarning("Budget year archive refused for {YearId}, actor {UserId}: {Reason}", id, user.Id, result.Refusal);
            SetError($"Failed to delete budget year: {result.Refusal}");
        }
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Groups/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroup(Guid budgetYearId, string name, [BindRequired] bool isRestricted)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed budget group form for {GroupOrYearId}", budgetYearId);
            return BadRequest(ModelState);
        }

        try
        {
            await budgetService.CreateGroupAsync(budgetYearId, name, isRestricted, user.Id);
            SetSuccess($"Group '{name}' created.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating budget group in year {YearId}", budgetYearId);
            SetError($"Failed to create group: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Groups/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateGroup(Guid id, string name, int sortOrder, [BindRequired] bool isRestricted)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed budget group form for {GroupOrYearId}", id);
            return BadRequest(ModelState);
        }

        try
        {
            await budgetService.UpdateGroupAsync(id, name, sortOrder, isRestricted, user.Id);
            SetSuccess($"Group '{name}' updated.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating budget group {GroupId}", id);
            SetError($"Failed to update group: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Groups/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteGroup(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await budgetService.DeleteGroupAsync(id, user.Id);
            SetSuccess("Group deleted.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting budget group {GroupId}", id);
            SetError($"Failed to delete group: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Categories/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategory(Guid budgetGroupId, string name, [BindRequired] decimal allocatedAmount,
        [BindRequired] ExpenditureType expenditureType, Guid? teamId, Guid budgetYearId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed budget category form for {EntityId}", budgetGroupId);
            return BadRequest(ModelState);
        }

        try
        {
            await budgetService.CreateCategoryAsync(budgetGroupId, name, allocatedAmount, expenditureType, teamId, user.Id);
            SetSuccess($"Category '{name}' created.");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating budget category in group {GroupId}", budgetGroupId);
            SetError($"Failed to create category: {ex.Message}");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
    }

    [HttpPost("Categories/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCategory(Guid id, string name, [BindRequired] decimal allocatedAmount,
        [BindRequired] ExpenditureType expenditureType)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed budget category form for {EntityId}", id);
            return BadRequest(ModelState);
        }

        try
        {
            await budgetService.UpdateCategoryAsync(id, name, allocatedAmount, expenditureType, user.Id);
            SetSuccess($"Category '{name}' updated.");
            return RedirectToAction(nameof(CategoryDetail), new { id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating budget category {CategoryId}", id);
            SetError($"Failed to update category: {ex.Message}");
            return RedirectToAction(nameof(CategoryDetail), new { id });
        }
    }

    [HttpPost("Categories/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(Guid id, Guid budgetYearId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await budgetService.DeleteCategoryAsync(id, user.Id);
            SetSuccess("Category deleted.");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting budget category {CategoryId}", id);
            SetError($"Failed to delete category: {ex.Message}");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
    }

    [HttpPost("LineItems/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLineItem(Guid budgetCategoryId, string description, [BindRequired] decimal amount,
        Guid? responsibleTeamId, string? notes, DateTime? expectedDate, [BindRequired] int vatRate)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed budget line item form for {EntityId}", budgetCategoryId);
            return BadRequest(ModelState);
        }

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        try
        {
            await budgetService.CreateLineItemAsync(budgetCategoryId, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);
            SetSuccess($"Line item '{description}' created.");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating line item in category {CategoryId}", budgetCategoryId);
            SetError($"Failed to create line item: {ex.Message}");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
    }

    [HttpPost("LineItems/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLineItem(Guid id, string description, [BindRequired] decimal amount,
        Guid? responsibleTeamId, string? notes, DateTime? expectedDate, [BindRequired] int vatRate, Guid budgetCategoryId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed budget line item form for {EntityId}", id);
            return BadRequest(ModelState);
        }

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        try
        {
            await budgetService.UpdateLineItemAsync(id, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);
            SetSuccess($"Line item '{description}' updated.");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating line item {LineItemId}", id);
            SetError($"Failed to update line item: {ex.Message}");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
    }

    [HttpPost("LineItems/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLineItem(Guid id, Guid budgetCategoryId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await budgetService.DeleteLineItemAsync(id, user.Id);
            SetSuccess("Line item deleted.");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting line item {LineItemId}", id);
            SetError($"Failed to delete line item: {ex.Message}");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
    }

    [HttpPost("Years/{id:guid}/EnsureTicketingGroup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnsureTicketingGroup(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var result = await budgetService.EnsureTicketingGroupAsync(id, user.Id);
            if (result.Created)
                SetSuccess(result.Message);
            else
                SetInfo(result.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error ensuring ticketing group for year {YearId}", id);
            SetError($"Failed to add ticketing group: {ex.Message}");
        }

        return RedirectToAction(nameof(YearDetail), new { id });
    }

    [HttpPost("TicketingProjection/{groupId:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTicketingProjection(Guid groupId, TicketingProjectionUpdateForm form)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed ticketing projection form for group {GroupId}", groupId);
            return BadRequest(ModelState);
        }

        try
        {
            var count = await ticketingBudgetService.UpdateProjectionAndRefreshAsync(
                new TicketingProjectionUpdateCommand(
                    groupId,
                    form.BudgetYearId,
                    form.StartDate.HasValue ? LocalDate.FromDateTime(form.StartDate.Value) : null,
                    form.EventDate.HasValue ? LocalDate.FromDateTime(form.EventDate.Value) : null,
                    form.InitialSalesCount,
                    form.DailySalesRate,
                    form.AverageTicketPrice,
                    form.VatRate,
                    form.StripeFeePercent,
                    form.StripeFeeFixed,
                    form.TicketTailorFeePercent),
                user.Id);
            if (count is null) return NotFound();
            SetSuccess($"Ticketing projection saved - {count} projected line item(s) generated.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating ticketing projection for group {GroupId}", groupId);
            SetError($"Failed to update projection: {ex.Message}");
        }

        return RedirectToAction(nameof(YearDetail), new { id = form.BudgetYearId });
    }
    [HttpPost("TicketingBudget/{yearId:guid}/Sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncTicketingBudget(Guid yearId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var count = await ticketingBudgetService.SyncActualsAsync(yearId, user.Id);
            if (count > 0)
                SetSuccess($"Synced {count} ticketing line item(s) from ticket sales data.");
            else
                SetInfo("No new ticket sales data to sync.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing ticketing budget for year {YearId}", yearId);
            SetError($"Failed to sync ticketing data: {ex.Message}");
        }

        return RedirectToAction(nameof(YearDetail), new { id = yearId });
    }

    private async Task<FinanceOverviewViewModel> BuildFinanceOverviewAsync(BudgetYearDetail year, IReadOnlyList<BudgetYearSummarySnapshot> allYears, CancellationToken ct)
    {
        var summary = budgetService.ComputeBudgetSummaryWithBuffers(year.Groups);

        var ticketingGroup = year.Groups.FirstOrDefault(g => g.IsTicketingGroup);
        if (ticketingGroup is not null)
        {
            var actualSold = budgetService.GetActualTicketsSold(ticketingGroup);
            ViewBag.ActualTicketsSold = actualSold;

            var projections = await budgetService.GetTicketingProjectionEntriesAsync(ticketingGroup.Id, ct);
            if (projections.Count > 0)
            {
                var projectedRemaining = projections.Sum(p => p.ProjectedTickets);
                var today = clock.GetCurrentInstant().InUtc().Date;
                var proj = ticketingGroup.TicketingProjection!;
                var daysToEvent = Period.Between(today, proj.EventDate!.Value, PeriodUnits.Days).Days;

                ViewBag.RemainingDays = Math.Max(0, daysToEvent);
                ViewBag.ProjectedRemaining = projectedRemaining;
                ViewBag.TotalTickets = actualSold + projectedRemaining;
            }
        }

        IReadOnlyDictionary<Guid, HoldedActualRow> holdedActuals = new Dictionary<Guid, HoldedActualRow>();
        if (int.TryParse(year.Year, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var calendarYear))
        {
            var actuals = await holdedFinance.GetActualsForYearAsync(calendarYear, ct);
            holdedActuals = actuals.ToDictionary(r => r.BudgetCategoryId);
        }

        return new FinanceOverviewViewModel
        {
            Year = year,
            AllYears = allYears,
            TotalIncome = summary.TotalIncome,
            TotalExpenses = summary.TotalExpenses,
            NetBalance = summary.NetBalance,
            IncomeSlices = summary.IncomeSlices,
            ExpenseSlices = summary.ExpenseSlices,
            HoldedActualsByCategory = holdedActuals
        };
    }
}
