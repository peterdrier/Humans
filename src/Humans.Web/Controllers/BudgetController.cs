using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Budget")]
public class BudgetController : HumansControllerBase
{
    private readonly IBudgetService _budgetService;
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<BudgetController> _logger;

    public BudgetController(
        IBudgetService budgetService,
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ILogger<BudgetController> logger)
        : base(userManager)
    {
        _budgetService = budgetService;
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var isFinanceAdmin = RoleChecks.IsFinanceAdmin(User);
            var coordinatorTeamIds = await _budgetService.GetEffectiveCoordinatorTeamIdsAsync(user.Id);

            if (!isFinanceAdmin && coordinatorTeamIds.Count == 0)
                return RedirectToAction(nameof(Summary));

            var activeYear = await _budgetService.GetActiveYearAsync();
            if (activeYear is null)
            {
                SetInfo("No active budget year.");
                return View("NoActiveBudget");
            }

            var model = new CoordinatorBudgetViewModel
            {
                Year = activeYear,
                EditableTeamIds = coordinatorTeamIds,
                IsFinanceAdmin = isFinanceAdmin
            };
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading coordinator budget view");
            SetError("Failed to load budget data.");
            return View("NoActiveBudget");
        }
    }

    [HttpGet("Summary")]
    public async Task<IActionResult> Summary()
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var activeYear = await _budgetService.GetActiveYearAsync();
            if (activeYear is null)
            {
                SetInfo("No active budget year.");
                return View("NoActiveBudget");
            }

            // Only show non-restricted groups in public summary
            var visibleGroups = activeYear.Groups.Where(g => !g.IsRestricted).ToList();
            var allLineItems = visibleGroups
                .SelectMany(g => g.Categories)
                .SelectMany(c => c.LineItems)
                .ToList();

            var totalLineItems = allLineItems.Sum(li => li.Amount);

            // Compute VAT projections for all line items
            var vatProjections = allLineItems
                .Where(li => li.VatRate > 0 && li.ExpectedDate.HasValue)
                .Select(li => new VatProjection
                {
                    SourceDescription = li.Description,
                    VatAmount = Math.Abs(li.Amount) * li.VatRate / 100m,
                    SettlementDate = ComputeVatSettlementDate(li.ExpectedDate!.Value),
                    VatRate = li.VatRate,
                    // Income line item -> VAT is an expense; Expense line item -> VAT is income
                    IsExpense = li.Amount > 0
                })
                .ToList();

            // Income = positive line items + VAT credits from expenses
            var income = allLineItems.Where(li => li.Amount > 0).Sum(li => li.Amount);
            var expenses = allLineItems.Where(li => li.Amount < 0).Sum(li => li.Amount); // negative
            var vatExpenses = vatProjections.Where(v => v.IsExpense).Sum(v => v.VatAmount);
            var vatCredits = vatProjections.Where(v => !v.IsExpense).Sum(v => v.VatAmount);

            var totalIncome = income + vatCredits;
            var totalExpenses = expenses - vatExpenses; // expenses is negative, subtract positive VAT expenses
            var netBalance = totalIncome + totalExpenses;

            // Build income slices (categories with positive totals)
            var incomeCategories = visibleGroups
                .SelectMany(g => g.Categories)
                .Select(c => new
                {
                    c.Name,
                    Total = c.LineItems.Where(li => li.Amount > 0).Sum(li => li.Amount)
                })
                .Where(c => c.Total > 0)
                .OrderByDescending(c => c.Total)
                .ToList();

            // Add VAT credits as income slice if any
            if (vatCredits > 0)
            {
                incomeCategories.Add(new { Name = "VAT Credits", Total = vatCredits });
            }

            var totalIncomeForSlices = incomeCategories.Sum(c => c.Total);
            var incomeSlices = incomeCategories
                .Select(c => new BudgetSlice
                {
                    Name = c.Name,
                    Amount = c.Total,
                    Percentage = totalIncomeForSlices > 0 ? c.Total / totalIncomeForSlices * 100 : 0
                })
                .ToList();

            // Build expense slices (categories with negative totals, displayed as absolute values)
            var expenseCategories = visibleGroups
                .SelectMany(g => g.Categories)
                .Select(c => new
                {
                    c.Name,
                    Total = Math.Abs(c.LineItems.Where(li => li.Amount < 0).Sum(li => li.Amount))
                })
                .Where(c => c.Total > 0)
                .OrderByDescending(c => c.Total)
                .ToList();

            // Add VAT expenses slice if any
            if (vatExpenses > 0)
            {
                expenseCategories.Add(new { Name = "VAT Liability", Total = vatExpenses });
            }

            // Add profit distribution if profitable (income > |expenses|)
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

            var coordinatorTeamIds = await _budgetService.GetEffectiveCoordinatorTeamIdsAsync(user.Id);

            var model = new BudgetSummaryViewModel
            {
                YearName = activeYear.Name,
                TotalIncome = totalIncome,
                TotalExpenses = totalExpenses,
                NetBalance = netBalance,
                TotalLineItems = totalLineItems,
                IncomeSlices = incomeSlices,
                ExpenseSlices = expenseSlices,
                IsCoordinator = coordinatorTeamIds.Count > 0 || RoleChecks.IsFinanceAdmin(User)
            };
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading budget summary");
            SetError("Failed to load budget summary.");
            return View("NoActiveBudget");
        }
    }

    /// <summary>
    /// Computes the VAT settlement date: ~6 weeks after the end of the quarter containing the expected date.
    /// </summary>
    private static LocalDate ComputeVatSettlementDate(LocalDate expectedDate)
    {
        var quarterEnd = expectedDate.Month switch
        {
            >= 1 and <= 3 => new LocalDate(expectedDate.Year, 3, 31),
            >= 4 and <= 6 => new LocalDate(expectedDate.Year, 6, 30),
            >= 7 and <= 9 => new LocalDate(expectedDate.Year, 9, 30),
            _ => new LocalDate(expectedDate.Year, 12, 31)
        };

        // ~6 weeks = 42 days after quarter end, roughly the 14th of the month after next
        return quarterEnd.PlusDays(45);
    }

    [HttpGet("Category/{id:guid}")]
    public async Task<IActionResult> CategoryDetail(Guid id)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var category = await _budgetService.GetCategoryByIdAsync(id);
            if (category is null) return NotFound();

            // Block access to restricted groups for non-finance users
            var isFinanceAdmin = RoleChecks.IsFinanceAdmin(User);
            if (category.BudgetGroup?.IsRestricted == true && !isFinanceAdmin)
                return Forbid();

            var coordinatorTeamIds = await _budgetService.GetEffectiveCoordinatorTeamIdsAsync(user.Id);
            if (!isFinanceAdmin && coordinatorTeamIds.Count == 0)
                return Forbid();

            var canEdit = category.TeamId.HasValue && coordinatorTeamIds.Contains(category.TeamId.Value);

            var teams = await _dbContext.Teams
                .Where(t => t.IsActive)
                .OrderBy(t => t.Name)
                .Select(t => new TeamOption { Id = t.Id, Name = t.Name })
                .ToListAsync();

            var model = new CoordinatorCategoryDetailViewModel
            {
                Category = category,
                CanEdit = canEdit || isFinanceAdmin,
                IsFinanceAdmin = isFinanceAdmin,
                Teams = teams
            };
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading budget category {CategoryId}", id);
            SetError("Failed to load category.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("LineItems/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLineItem(Guid budgetCategoryId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, DateTime? expectedDate, int vatRate)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var authResult = await AuthorizeCategoryEditAsync(budgetCategoryId, user.Id);
        if (authResult is not null) return authResult;

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        try
        {
            await _budgetService.CreateLineItemAsync(budgetCategoryId, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);
            SetSuccess($"Line item '{description}' created.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating line item in category {CategoryId}", budgetCategoryId);
            SetError($"Failed to create line item: {ex.Message}");
        }
        return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
    }

    [HttpPost("LineItems/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLineItem(Guid id, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, DateTime? expectedDate, int vatRate, Guid budgetCategoryId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var lineItem = await _dbContext.BudgetLineItems.FindAsync(id);
        if (lineItem is null) return NotFound();

        var authResult = await AuthorizeCategoryEditAsync(lineItem.BudgetCategoryId, user.Id);
        if (authResult is not null) return authResult;

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        try
        {
            await _budgetService.UpdateLineItemAsync(id, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);
            SetSuccess($"Line item '{description}' updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating line item {LineItemId}", id);
            SetError($"Failed to update line item: {ex.Message}");
        }
        return RedirectToAction(nameof(CategoryDetail), new { id = lineItem.BudgetCategoryId });
    }

    [HttpPost("LineItems/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLineItem(Guid id, Guid budgetCategoryId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var lineItem = await _dbContext.BudgetLineItems.FindAsync(id);
        if (lineItem is null) return NotFound();

        var authResult = await AuthorizeCategoryEditAsync(lineItem.BudgetCategoryId, user.Id);
        if (authResult is not null) return authResult;

        try
        {
            await _budgetService.DeleteLineItemAsync(id, user.Id);
            SetSuccess("Line item deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting line item {LineItemId}", id);
            SetError($"Failed to delete line item: {ex.Message}");
        }
        return RedirectToAction(nameof(CategoryDetail), new { id = lineItem.BudgetCategoryId });
    }

    private async Task<IActionResult?> AuthorizeCategoryEditAsync(Guid categoryId, Guid userId)
    {
        if (RoleChecks.IsFinanceAdmin(User))
            return null;

        var category = await _budgetService.GetCategoryByIdAsync(categoryId);
        if (category is null) return NotFound();

        if (category.BudgetGroup?.BudgetYear?.IsDeleted == true) return NotFound();

        if (category.BudgetGroup?.IsRestricted == true)
            return Forbid();

        if (!category.TeamId.HasValue)
            return Forbid();

        var coordinatorTeamIds = await _budgetService.GetEffectiveCoordinatorTeamIdsAsync(userId);
        if (!coordinatorTeamIds.Contains(category.TeamId.Value))
        {
            SetError("You can only edit line items in your own department's budget.");
            return RedirectToAction(nameof(CategoryDetail), new { id = categoryId });
        }

        return null;
    }
}
