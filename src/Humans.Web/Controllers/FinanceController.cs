using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
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
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<FinanceController> _logger;

    public FinanceController(
        IBudgetService budgetService,
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ILogger<FinanceController> logger)
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
            var activeYear = await _budgetService.GetActiveYearAsync();
            if (activeYear is null)
            {
                ViewBag.Years = await _budgetService.GetAllYearsAsync();
                return View("NoActiveYear");
            }

            ViewBag.AllYears = await _budgetService.GetAllYearsAsync();
            return View("YearDetail", activeYear);
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

            ViewBag.AllYears = await _budgetService.GetAllYearsAsync();
            return View(year);
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
            ViewBag.Years = await _budgetService.GetAllYearsAsync();
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
            var years = await _budgetService.GetAllYearsAsync();
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
        Guid? responsibleTeamId, string? notes, DateTime? expectedDate)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        try
        {
            await _budgetService.CreateLineItemAsync(budgetCategoryId, description, amount, responsibleTeamId, notes, nodaDate, user.Id);
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
        Guid? responsibleTeamId, string? notes, DateTime? expectedDate, Guid budgetCategoryId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var nodaDate = expectedDate.HasValue ? LocalDate.FromDateTime(expectedDate.Value) : (LocalDate?)null;

        try
        {
            await _budgetService.UpdateLineItemAsync(id, description, amount, responsibleTeamId, notes, nodaDate, user.Id);
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
}
