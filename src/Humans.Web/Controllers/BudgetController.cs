using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Teams;
using Humans.Web.Authorization;
using Humans.Web.Authorization.Requirements;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Budget")]
public class BudgetController(
    IBudgetService budgetService,
    ITeamServiceRead teamService,
    IAuthorizationService authService,
    IUserService userService,
    ILogger<BudgetController> logger) : HumansControllerBase(userService)
{
    private readonly ITeamServiceRead _teamService = teamService;

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync();
            if (errorResult is not null) return errorResult;

            var isFinanceAdmin = (await authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)).Succeeded;
            var data = await budgetService.GetCoordinatorBudgetViewDataAsync(user.Id, isFinanceAdmin);

            if (data.ShouldRedirectToSummary)
                return RedirectToAction(nameof(Summary));

            if (data.Year is null)
            {
                SetInfo("No active budget year.");
                return View("NoActiveBudget");
            }

            var model = new CoordinatorBudgetViewModel
            {
                Year = data.Year,
                EditableTeamIds = data.EditableTeamIds,
                IsFinanceAdmin = data.IsFinanceAdmin
            };
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading coordinator budget view");
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

            var activeYear = await budgetService.GetActiveYearAsync();
            if (activeYear is null)
            {
                SetInfo("No active budget year.");
                return View("NoActiveBudget");
            }

            var visibleGroups = activeYear.Groups.ToList();
            var summary = budgetService.ComputeBudgetSummaryWithBuffers(visibleGroups);

            var totalLineItems = visibleGroups
                .SelectMany(g => g.Categories)
                .SelectMany(c => c.LineItems)
                .Where(li => !li.IsCashflowOnly)
                .Sum(li => li.Amount);

            var coordinatorTeamIds = await budgetService.GetEffectiveCoordinatorTeamIdsAsync(user.Id);

            var model = new BudgetSummaryViewModel
            {
                YearName = activeYear.Name,
                TotalIncome = summary.TotalIncome,
                TotalExpenses = summary.TotalExpenses,
                NetBalance = summary.NetBalance,
                TotalLineItems = totalLineItems,
                IncomeSlices = summary.IncomeSlices.Select(s => new BudgetSlice { Name = s.Name, Amount = s.Amount, Percentage = s.Percentage }).ToList(),
                ExpenseSlices = summary.ExpenseSlices.Select(s => new BudgetSlice { Name = s.Name, Amount = s.Amount, Percentage = s.Percentage }).ToList(),
                IsCoordinator = coordinatorTeamIds.Count > 0 || (await authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)).Succeeded
            };
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading budget summary");
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

            var isFinanceAdmin = (await authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)).Succeeded;
            var detail = await budgetService.GetCoordinatorCategoryDetailViewDataAsync(id, user.Id, isFinanceAdmin);
            if (detail.Category is null) return NotFound();
            if (detail.ShouldForbid)
                return Forbid();

            var canEdit = (await authService.AuthorizeAsync(User, detail.Category, BudgetOperationRequirement.Edit)).Succeeded;

            var model = new CoordinatorCategoryDetailViewModel
            {
                Category = detail.Category,
                CanEdit = canEdit,
                IsFinanceAdmin = isFinanceAdmin,
                Teams = detail.Teams
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .ToList()
            };
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading budget category {CategoryId}", id);
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

        var authResult = await AuthorizeCategoryEditAsync(budgetCategoryId);
        if (authResult is not null) return authResult;

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        var result = await budgetService.CreateLineItemWithResultAsync(
            budgetCategoryId, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);

        if (result.Succeeded)
            SetSuccess($"Line item '{description}' created.");
        else
            SetError($"Failed to create line item: {result.ErrorMessage}");

        return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
    }

    [HttpPost("LineItems/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLineItem(Guid id, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, DateTime? expectedDate, int vatRate, Guid budgetCategoryId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var lineItem = await budgetService.GetLineItemByIdAsync(id);
        if (lineItem is null) return NotFound();

        var authResult = await AuthorizeCategoryEditAsync(lineItem.BudgetCategoryId);
        if (authResult is not null) return authResult;

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        var result = await budgetService.UpdateLineItemWithResultAsync(
            id, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);

        if (result.Succeeded)
            SetSuccess($"Line item '{description}' updated.");
        else
            SetError($"Failed to update line item: {result.ErrorMessage}");

        return RedirectToAction(nameof(CategoryDetail), new { id = lineItem.BudgetCategoryId });
    }

    [HttpPost("LineItems/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLineItem(Guid id, Guid budgetCategoryId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var lineItem = await budgetService.GetLineItemByIdAsync(id);
        if (lineItem is null) return NotFound();

        var authResult = await AuthorizeCategoryEditAsync(lineItem.BudgetCategoryId);
        if (authResult is not null) return authResult;

        try
        {
            await budgetService.DeleteLineItemAsync(id, user.Id);
            SetSuccess("Line item deleted.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting line item {LineItemId}", id);
            SetError($"Failed to delete line item: {ex.Message}");
        }
        return RedirectToAction(nameof(CategoryDetail), new { id = lineItem.BudgetCategoryId });
    }

    /// <summary>Load category + auth-check Edit; returns IActionResult on deny, null on allow.</summary>
    private async Task<IActionResult?> AuthorizeCategoryEditAsync(Guid categoryId)
    {
        var category = await budgetService.GetCategoryByIdAsync(categoryId);
        if (category is null) return NotFound();

        var result = await authService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit);
        if (!result.Succeeded)
        {
            SetError("You do not have permission to edit this budget category.");
            return RedirectToAction(nameof(CategoryDetail), new { id = categoryId });
        }

        return null;
    }
}
