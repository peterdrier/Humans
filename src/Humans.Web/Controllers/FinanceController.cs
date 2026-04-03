using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.FinanceAdminOrAdmin)]
[Route("Finance")]
public class FinanceController : HumansControllerBase
{
    private readonly IBudgetService _budgetService;
    private readonly ITicketingBudgetService _ticketingBudgetService;
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<FinanceController> _logger;

    public FinanceController(
        IBudgetService budgetService,
        ITicketingBudgetService ticketingBudgetService,
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ILogger<FinanceController> logger)
        : base(userManager)
    {
        _budgetService = budgetService;
        _ticketingBudgetService = ticketingBudgetService;
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            var activeYear = await _budgetService.GetActiveYearAsync();
            if (activeYear is null)
            {
                ViewBag.Years = await _budgetService.GetAllYearsAsync();
                return View("NoActiveYear");
            }

            var allYears = await _budgetService.GetAllYearsAsync();
            var model = BuildFinanceOverview(activeYear, allYears);
            return View("YearDetail", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading finance index");
            SetError("Failed to load budget data.");
            return View("NoActiveYear");
        }
    }

    [HttpGet("Years/{id:guid}")]
    public async Task<IActionResult> YearDetail(Guid id)
    {
        try
        {
            var year = await _budgetService.GetYearByIdAsync(id);
            if (year is null) return NotFound();

            var allYears = await _budgetService.GetAllYearsAsync();
            var model = BuildFinanceOverview(year, allYears);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading budget year {YearId}", id);
            SetError("Failed to load budget year.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("Categories/{id:guid}")]
    public async Task<IActionResult> CategoryDetail(Guid id)
    {
        try
        {
            var category = await _budgetService.GetCategoryByIdAsync(id);
            if (category is null) return NotFound();

            ViewBag.Teams = await _dbContext.Teams
                .Where(t => t.IsActive)
                .OrderBy(t => t.Name)
                .Select(t => new { t.Id, t.Name })
                .ToListAsync();

            return View(category);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading budget category {CategoryId}", id);
            SetError("Failed to load category.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("AuditLog/{yearId:guid?}")]
    public async Task<IActionResult> AuditLog(Guid? yearId)
    {
        try
        {
            if (!yearId.HasValue)
            {
                var active = await _budgetService.GetActiveYearAsync();
                yearId = active?.Id;
            }

            var entries = await _budgetService.GetAuditLogAsync(yearId);
            ViewBag.YearId = yearId;
            ViewBag.Years = await _budgetService.GetAllYearsAsync(includeArchived: true);
            return View(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading budget audit log");
            SetError("Failed to load audit log.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("Admin")]
    public async Task<IActionResult> Admin()
    {
        try
        {
            var years = await _budgetService.GetAllYearsAsync(includeArchived: true);
            return View(years);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading finance admin");
            SetError("Failed to load finance admin.");
            return RedirectToAction(nameof(Index));
        }
    }

    // --- POST Actions ---

    [HttpPost("Years/{id:guid}/SyncDepartments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncDepartments(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var count = await _budgetService.SyncDepartmentsAsync(id, user.Id);
            if (count > 0)
                SetSuccess($"Synced {count} new department(s) into budget.");
            else
                SetInfo("All budget-enabled teams are already in the Departments group.");
            return RedirectToAction(nameof(YearDetail), new { id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing departments for year {YearId}", id);
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
            await _budgetService.CreateYearAsync(year, name, user.Id);
            SetSuccess($"Budget year '{name}' created.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating budget year {Year}", year);
            SetError($"Failed to create budget year: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Years/{id:guid}/UpdateStatus")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateYearStatus(Guid id, BudgetYearStatus status)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.UpdateYearStatusAsync(id, status, user.Id);
            SetSuccess($"Budget year status updated to {status}.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating budget year {YearId} status to {Status}", id, status);
            SetError($"Failed to update status: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Years/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateYear(Guid id, string year, string name)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.UpdateYearAsync(id, year, name, user.Id);
            SetSuccess("Budget year updated.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating budget year {YearId}", id);
            SetError($"Failed to update budget year: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Years/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteYear(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.DeleteYearAsync(id, user.Id);
            SetSuccess("Budget year deleted.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting budget year {YearId}", id);
            SetError($"Failed to delete budget year: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Groups/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroup(Guid budgetYearId, string name, bool isRestricted)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.CreateGroupAsync(budgetYearId, name, isRestricted, user.Id);
            SetSuccess($"Group '{name}' created.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating budget group in year {YearId}", budgetYearId);
            SetError($"Failed to create group: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Groups/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateGroup(Guid id, string name, int sortOrder, bool isRestricted)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.UpdateGroupAsync(id, name, sortOrder, isRestricted, user.Id);
            SetSuccess($"Group '{name}' updated.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating budget group {GroupId}", id);
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
            await _budgetService.DeleteGroupAsync(id, user.Id);
            SetSuccess("Group deleted.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting budget group {GroupId}", id);
            SetError($"Failed to delete group: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Categories/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategory(Guid budgetGroupId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid? teamId, Guid budgetYearId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.CreateCategoryAsync(budgetGroupId, name, allocatedAmount, expenditureType, teamId, user.Id);
            SetSuccess($"Category '{name}' created.");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating budget category in group {GroupId}", budgetGroupId);
            SetError($"Failed to create category: {ex.Message}");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
    }

    [HttpPost("Categories/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCategory(Guid id, string name, decimal allocatedAmount,
        ExpenditureType expenditureType)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.UpdateCategoryAsync(id, name, allocatedAmount, expenditureType, user.Id);
            SetSuccess($"Category '{name}' updated.");
            return RedirectToAction(nameof(CategoryDetail), new { id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating budget category {CategoryId}", id);
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
            await _budgetService.DeleteCategoryAsync(id, user.Id);
            SetSuccess("Category deleted.");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting budget category {CategoryId}", id);
            SetError($"Failed to delete category: {ex.Message}");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
    }

    [HttpPost("LineItems/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLineItem(Guid budgetCategoryId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, DateTime? expectedDate, int vatRate)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        try
        {
            await _budgetService.CreateLineItemAsync(budgetCategoryId, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);
            SetSuccess($"Line item '{description}' created.");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating line item in category {CategoryId}", budgetCategoryId);
            SetError($"Failed to create line item: {ex.Message}");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
    }

    [HttpPost("LineItems/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLineItem(Guid id, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, DateTime? expectedDate, int vatRate, Guid budgetCategoryId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        try
        {
            await _budgetService.UpdateLineItemAsync(id, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);
            SetSuccess($"Line item '{description}' updated.");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating line item {LineItemId}", id);
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
            await _budgetService.DeleteLineItemAsync(id, user.Id);
            SetSuccess("Line item deleted.");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting line item {LineItemId}", id);
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
            var added = await _budgetService.EnsureTicketingGroupAsync(id, user.Id);
            if (added)
                SetSuccess("Ticketing group added to this budget year.");
            else
                SetInfo("Ticketing group already exists for this budget year.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring ticketing group for year {YearId}", id);
            SetError($"Failed to add ticketing group: {ex.Message}");
        }

        return RedirectToAction(nameof(YearDetail), new { id });
    }

    [HttpPost("TicketingProjection/{groupId:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTicketingProjection(Guid groupId, DateTime? startDate, DateTime? eventDate,
        int initialSalesCount, decimal dailySalesRate, decimal averageTicketPrice, int vatRate,
        decimal stripeFeePercent, decimal stripeFeeFixed, decimal ticketTailorFeePercent, Guid budgetYearId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var nodaStart = startDate.HasValue ? LocalDate.FromDateTime(startDate.Value) : (LocalDate?)null;
        var nodaEvent = eventDate.HasValue ? LocalDate.FromDateTime(eventDate.Value) : (LocalDate?)null;

        try
        {
            await _budgetService.UpdateTicketingProjectionAsync(groupId, nodaStart, nodaEvent,
                initialSalesCount, dailySalesRate, averageTicketPrice, vatRate,
                stripeFeePercent, stripeFeeFixed, ticketTailorFeePercent, user.Id);

            // Refresh projections after saving parameters (no actuals sync needed)
            var count = await _ticketingBudgetService.RefreshProjectionsAsync(budgetYearId);
            SetSuccess($"Ticketing projection saved — {count} projected line item(s) generated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating ticketing projection for group {GroupId}", groupId);
            SetError($"Failed to update projection: {ex.Message}");
        }

        return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
    }

    [HttpPost("TicketingBudget/{yearId:guid}/Sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncTicketingBudget(Guid yearId)
    {
        try
        {
            var count = await _ticketingBudgetService.SyncActualsAsync(yearId);
            if (count > 0)
                SetSuccess($"Synced {count} ticketing line item(s) from ticket sales data.");
            else
                SetInfo("No new ticket sales data to sync.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing ticketing budget for year {YearId}", yearId);
            SetError($"Failed to sync ticketing data: {ex.Message}");
        }

        return RedirectToAction(nameof(YearDetail), new { id = yearId });
    }

    /// <summary>
    /// Builds the FinanceOverviewViewModel with inline summary data so FinanceAdmin
    /// sees everything on one page without navigating to /Budget/Summary.
    /// </summary>
    private static FinanceOverviewViewModel BuildFinanceOverview(BudgetYear year, IReadOnlyList<BudgetYear> allYears)
    {
        // All groups (including restricted) for FinanceAdmin summary
        var allLineItems = year.Groups
            .SelectMany(g => g.Categories)
            .SelectMany(c => c.LineItems)
            .ToList();

        var budgetLineItems = allLineItems.Where(li => !li.IsCashflowOnly).ToList();

        // Compute VAT projections
        var vatProjections = budgetLineItems
            .Where(li => li.VatRate > 0 && li.ExpectedDate.HasValue)
            .Select(li => new
            {
                VatAmount = Math.Abs(li.Amount) * li.VatRate / (100m + li.VatRate),
                IsExpense = li.Amount > 0 // Income generates VAT liability (expense)
            })
            .ToList();

        var income = budgetLineItems.Where(li => li.Amount > 0).Sum(li => li.Amount);
        var expenses = budgetLineItems.Where(li => li.Amount < 0).Sum(li => li.Amount);
        var vatExpenses = vatProjections.Where(v => v.IsExpense).Sum(v => v.VatAmount);
        var vatCredits = vatProjections.Where(v => !v.IsExpense).Sum(v => v.VatAmount);

        var totalIncome = income + vatCredits;
        var totalExpenses = expenses - vatExpenses;
        var netBalance = totalIncome + totalExpenses;

        // Build income slices
        var incomeCategories = year.Groups
            .SelectMany(g => g.Categories)
            .Select(c => new
            {
                c.Name,
                Total = c.LineItems.Where(li => li.Amount > 0 && !li.IsCashflowOnly).Sum(li => li.Amount)
            })
            .Where(c => c.Total > 0)
            .OrderByDescending(c => c.Total)
            .ToList();

        if (vatCredits > 0)
            incomeCategories.Add(new { Name = "VAT Credits", Total = vatCredits });

        var totalIncomeForSlices = incomeCategories.Sum(c => c.Total);
        var incomeSlices = incomeCategories
            .Select(c => new BudgetSlice
            {
                Name = c.Name,
                Amount = c.Total,
                Percentage = totalIncomeForSlices > 0 ? c.Total / totalIncomeForSlices * 100 : 0
            })
            .ToList();

        // Build expense slices
        var expenseCategories = year.Groups
            .SelectMany(g => g.Categories)
            .Select(c => new
            {
                c.Name,
                Total = Math.Abs(c.LineItems.Where(li => li.Amount < 0 && !li.IsCashflowOnly).Sum(li => li.Amount))
            })
            .Where(c => c.Total > 0)
            .OrderByDescending(c => c.Total)
            .ToList();

        if (vatExpenses > 0)
            expenseCategories.Add(new { Name = "VAT Liability", Total = vatExpenses });

        var profit = income + vatCredits - (Math.Abs(expenses) + vatExpenses);
        if (profit > 0)
        {
            expenseCategories.Add(new { Name = "Cash Reserves (90%)", Total = profit * 0.9m });
            expenseCategories.Add(new { Name = "Spanish Taxes (10%)", Total = profit * 0.1m });
        }

        var totalExpenseForSlices = expenseCategories.Sum(c => c.Total);
        var expenseSlices = expenseCategories
            .Select(c => new BudgetSlice
            {
                Name = c.Name,
                Amount = c.Total,
                Percentage = totalExpenseForSlices > 0 ? c.Total / totalExpenseForSlices * 100 : 0
            })
            .ToList();

        return new FinanceOverviewViewModel
        {
            Year = year,
            AllYears = allYears,
            TotalIncome = totalIncome,
            TotalExpenses = totalExpenses,
            NetBalance = netBalance,
            IncomeSlices = incomeSlices,
            ExpenseSlices = expenseSlices
        };
    }
}
