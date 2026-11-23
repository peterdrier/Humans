using Humans.Finance.Contracts;
using Humans.Budget.Contracts;
using Humans.Permissions.Contracts;
using Humans.Budget.Services;
using Humans.Base.Controllers;
using Humans.Budget.Authorization;
using Humans.Budget.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Budget.Controllers;

[Authorize]
[Route("Budget")]
internal sealed class BudgetController(
    IPermissionService permissions,
    IBudgetManagement budgetService,
    IAuthorizationService authService,
    IUserService userService,
    IStringLocalizer<BudgetResource> localizer,
    ILogger<BudgetController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync(ct);
            if (errorResult is not null) return errorResult;

            var isFinanceAdmin = (await authService.AuthorizeAsync(User, FinancePolicies.Manage)).Succeeded;
            var editableTeamIds = (await permissions.GetOwnersAsync(user.Id, BudgetPermissions.Edit, ct))
                .Select(o => o.Id).ToHashSet();
            if (!isFinanceAdmin && editableTeamIds.Count == 0)
                return RedirectToAction(nameof(Summary));

            var activeYear = await budgetService.GetActiveYearAsync(ct);
            if (activeYear is null)
            {
                SetInfo(localizer["Budget_Flash_NoActiveYear"].Value);
                return View("NoActiveBudget");
            }

            var model = new CoordinatorBudgetViewModel
            {
                Year = activeYear,
                EditableTeamIds = editableTeamIds,
                IsFinanceAdmin = isFinanceAdmin
            };
            return View(model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading coordinator budget view");
            SetError(localizer["Budget_Flash_LoadFailed"].Value);
            return View("NoActiveBudget");
        }
    }

    [HttpGet("Summary")]
    public async Task<IActionResult> Summary()
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync(ct);
            if (errorResult is not null) return errorResult;

            var activeYear = await budgetService.GetActiveYearAsync(ct);
            if (activeYear is null)
            {
                SetInfo(localizer["Budget_Flash_NoActiveYear"].Value);
                return View("NoActiveBudget");
            }

            var visibleGroups = activeYear.Groups.OrderBy(group => group.SortOrder).ToList();
            var summary = budgetService.ComputeBudgetSummaryWithBuffers(visibleGroups);

            var canEditSomewhere = (await permissions.GetOwnersAsync(user.Id, BudgetPermissions.Edit, ct)).Count > 0;

            var model = new BudgetSummaryViewModel
            {
                YearName = activeYear.Name,
                TotalIncome = summary.TotalIncome,
                TotalExpenses = summary.TotalExpenses,
                NetBalance = summary.NetBalance,
                IncomeSlices = summary.IncomeSlices.OrderBy(slice => slice.BudgetCategoryId is null)
                    .ThenByDescending(slice => slice.BudgetCategoryId is null ? 0m : slice.Amount).ToList(),
                ExpenseSlices = summary.ExpenseSlices.OrderBy(slice => slice.BudgetCategoryId is null)
                    .ThenByDescending(slice => slice.BudgetCategoryId is null ? 0m : slice.Amount).ToList(),
                IsCoordinator = canEditSomewhere || (await authService.AuthorizeAsync(User, FinancePolicies.Manage)).Succeeded
            };
            return View(model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading budget summary");
            SetError(localizer["Budget_Flash_SummaryLoadFailed"].Value);
            return View("NoActiveBudget");
        }
    }

    [HttpGet("Category/{id:guid}")]
    public async Task<IActionResult> CategoryDetail(Guid id)
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var (errorResult, user) = await RequireCurrentUserAsync(ct);
            if (errorResult is not null) return errorResult;

            var isFinanceAdmin = (await authService.AuthorizeAsync(User, FinancePolicies.Manage)).Succeeded;
            var category = await budgetService.GetCategoryByIdAsync(id, ct);
            if (category is null) return NotFound();
            if (!(await authService.AuthorizeAsync(User, category, BudgetOperationRequirement.View)).Succeeded)
                return Forbid();

            var canEdit = (await authService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)).Succeeded;

            var model = new CoordinatorCategoryDetailViewModel
            {
                Category = category,
                CanEdit = canEdit,
                IsFinanceAdmin = isFinanceAdmin,
                Teams = (await budgetService.GetActiveTeamsAsync(ct))
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .ToList()
            };
            return View(model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading budget category {CategoryId}", id);
            SetError(localizer["Budget_Flash_CategoryLoadFailed"].Value);
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("LineItems/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLineItem(Guid budgetCategoryId, string description, [BindRequired] decimal amount,
        Guid? responsibleTeamId, string? notes, DateTime? expectedDate, [BindRequired] int vatRate)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;

        var authResult = await AuthorizeCategoryEditAsync(budgetCategoryId);
        if (authResult is not null) return authResult;

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed budget line item form");
            return BadRequest(ModelState);
        }

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        try
        {
            var result = await budgetService.CreateLineItemAsync(
                budgetCategoryId, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);
            if (result.Refusal is not null)
            {
                logger.LogWarning("Rejected budget line item input for actor {ActorId}", user.Id);
                SetError(localizer["Budget_Flash_LineCreateFailed"].Value);
                return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
            }
            SetSuccess(localizer["Budget_Flash_LineCreated", description].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create line item in category {CategoryId}", budgetCategoryId);
            SetError(localizer["Budget_Flash_LineCreateFailed"].Value);
        }

        return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
    }

    [HttpPost("LineItems/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLineItem(Guid id, string description, [BindRequired] decimal amount,
        Guid? responsibleTeamId, string? notes, DateTime? expectedDate, [BindRequired] int vatRate)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;

        var categoryId = await budgetService.GetLineItemCategoryIdAsync(id, HttpContext.RequestAborted);
        if (categoryId is null) return NotFound();

        var authResult = await AuthorizeCategoryEditAsync(categoryId.Value);
        if (authResult is not null) return authResult;

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed budget line item form");
            return BadRequest(ModelState);
        }

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        try
        {
            var result = await budgetService.UpdateLineItemAsync(
                id, description, amount, responsibleTeamId, notes, nodaDate, vatRate, user.Id);
            if (result.Refusal is not null)
            {
                logger.LogWarning("Rejected budget line item input for actor {ActorId}", user.Id);
                SetError(localizer["Budget_Flash_LineUpdateFailed"].Value);
                return RedirectToAction(nameof(CategoryDetail), new { id = categoryId.Value });
            }
            SetSuccess(localizer["Budget_Flash_LineUpdated", description].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update line item {LineItemId}", id);
            SetError(localizer["Budget_Flash_LineUpdateFailed"].Value);
        }

        return RedirectToAction(nameof(CategoryDetail), new { id = categoryId.Value });
    }

    [HttpPost("LineItems/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLineItem(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(HttpContext.RequestAborted);
        if (errorResult is not null) return errorResult;

        var categoryId = await budgetService.GetLineItemCategoryIdAsync(id, HttpContext.RequestAborted);
        if (categoryId is null) return NotFound();

        var authResult = await AuthorizeCategoryEditAsync(categoryId.Value);
        if (authResult is not null) return authResult;

        try
        {
            var result = await budgetService.DeleteLineItemAsync(id, user.Id);
            if (result.Refusal is not null)
            {
                logger.LogWarning("Budget line item deletion refused for {LineItemId}, actor {ActorId}", id, user.Id);
                SetError(localizer["Budget_Flash_LineDeleteFailed"].Value);
            }
            else
                SetSuccess(localizer["Budget_Flash_LineDeleted"].Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete line item {LineItemId}", id);
            SetError(localizer["Budget_Flash_LineDeleteFailed"].Value);
        }
        return RedirectToAction(nameof(CategoryDetail), new { id = categoryId.Value });
    }

    private async Task<IActionResult?> AuthorizeCategoryEditAsync(Guid categoryId)
    {
        var category = await budgetService.GetCategoryByIdAsync(categoryId, HttpContext.RequestAborted);
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
