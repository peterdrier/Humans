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
    private readonly IAuthorizationService _authService;
    private readonly ILogger<BudgetController> _logger;

    public BudgetController(
        IBudgetService budgetService,
        HumansDbContext dbContext,
        IAuthorizationService authService,
        UserManager<User> userManager,
        ILogger<BudgetController> logger)
        : base(userManager)
    {
        _budgetService = budgetService;
        _dbContext = dbContext;
        _authService = authService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var isFinanceAdmin = (await _authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)).Succeeded;
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

            var visibleGroups = activeYear.Groups.ToList();
            var summary = _budgetService.ComputeBudgetSummaryWithBuffers(visibleGroups);

            var totalLineItems = visibleGroups
                .SelectMany(g => g.Categories)
                .SelectMany(c => c.LineItems)
                .Where(li => !li.IsCashflowOnly)
                .Sum(li => li.Amount);

            var coordinatorTeamIds = await _budgetService.GetEffectiveCoordinatorTeamIdsAsync(user.Id);

            var model = new BudgetSummaryViewModel
            {
                YearName = activeYear.Name,
                TotalIncome = summary.TotalIncome,
                TotalExpenses = summary.TotalExpenses,
                NetBalance = summary.NetBalance,
                TotalLineItems = totalLineItems,
                IncomeSlices = summary.IncomeSlices.Select(s => new BudgetSlice { Name = s.Name, Amount = s.Amount, Percentage = s.Percentage }).ToList(),
                ExpenseSlices = summary.ExpenseSlices.Select(s => new BudgetSlice { Name = s.Name, Amount = s.Amount, Percentage = s.Percentage }).ToList(),
                IsCoordinator = coordinatorTeamIds.Count > 0 || (await _authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)).Succeeded
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

    [HttpGet("Category/{id:guid}")]
    public async Task<IActionResult> CategoryDetail(Guid id)
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var category = await _budgetService.GetCategoryByIdAsync(id);
            if (category is null) return NotFound();

            // Block access to restricted/ticketing groups for non-finance users
            var isFinanceAdmin = (await _authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)).Succeeded;
            if ((category.BudgetGroup?.IsRestricted == true || category.BudgetGroup?.IsTicketingGroup == true) && !isFinanceAdmin)
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
        if ((await _authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)).Succeeded)
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
