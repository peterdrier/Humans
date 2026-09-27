using Humans.Budget.Services;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Budget.Authorization;
using Humans.Budget.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Budget.Controllers;

[Authorize]
[Route("Budget")]
internal sealed class BudgetController(
    IBudgetService budgetService,
    IAuthorizationService authService,
    IUserServiceRead userService,
    IStringLocalizer<BudgetResource> localizer,
    ILogger<BudgetController> logger) : HumansControllerBase(userService)
{
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
                SetInfo(localizer["Budget_Flash_NoActiveYear"].Value);
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
            SetError(localizer["Budget_Flash_LoadFailed"].Value);
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
                SetInfo(localizer["Budget_Flash_NoActiveYear"].Value);
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
            SetError(localizer["Budget_Flash_SummaryLoadFailed"].Value);
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
            SetError(localizer["Budget_Flash_CategoryLoadFailed"].Value);
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

        try
        {
            await budgetService.CreateLineItemAsync(
                budgetCategoryId, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);
            SetSuccess(localizer["Budget_Flash_LineCreated", description].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create line item in category {CategoryId}", budgetCategoryId);
            SetError(localizer["Budget_Flash_LineCreateFailed", ex.Message].Value);
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

        var lineItem = await budgetService.GetLineItemByIdAsync(id);
        if (lineItem is null) return NotFound();

        var authResult = await AuthorizeCategoryEditAsync(lineItem.BudgetCategoryId);
        if (authResult is not null) return authResult;

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        try
        {
            await budgetService.UpdateLineItemAsync(
                id, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);
            SetSuccess(localizer["Budget_Flash_LineUpdated", description].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update line item {LineItemId}", id);
            SetError(localizer["Budget_Flash_LineUpdateFailed", ex.Message].Value);
        }

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
            SetSuccess(localizer["Budget_Flash_LineDeleted"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete line item {LineItemId}", id);
            SetError(localizer["Budget_Flash_LineDeleteFailed", ex.Message].Value);
        }
        return RedirectToAction(nameof(CategoryDetail), new { id = lineItem.BudgetCategoryId });
    }

    private async Task<IActionResult?> AuthorizeCategoryEditAsync(Guid categoryId)
    {
        var category = await budgetService.GetCategoryByIdAsync(categoryId);
        if (category is null) return NotFound();

        var result = await authService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit);
        if (!result.Succeeded)
        {
            SetError(localizer["Budget_Flash_CategoryEditDenied"].Value);
            return RedirectToAction(nameof(CategoryDetail), new { id = categoryId });
        }

        return null;
    }
}
