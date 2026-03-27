using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Service for managing budget years, groups, categories, and line items with integrated audit logging.
/// </summary>
public class BudgetService : IBudgetService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<BudgetService> _logger;

    public BudgetService(
        HumansDbContext dbContext,
        IClock clock,
        ILogger<BudgetService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    // ───────────────────────── Budget Years ─────────────────────────

    public async Task<IReadOnlyList<BudgetYear>> GetAllYearsAsync()
    {
        return await _dbContext.BudgetYears
            .Include(y => y.Groups)
                .ThenInclude(g => g.Categories)
            .OrderByDescending(y => y.Year)
            .ToListAsync();
    }

    public async Task<BudgetYear?> GetYearByIdAsync(Guid id)
    {
        return await _dbContext.BudgetYears
            .Include(y => y.Groups.OrderBy(g => g.SortOrder))
                .ThenInclude(g => g.Categories.OrderBy(c => c.SortOrder))
                    .ThenInclude(c => c.LineItems.OrderBy(li => li.SortOrder))
            .Include(y => y.Groups)
                .ThenInclude(g => g.Categories)
                    .ThenInclude(c => c.Team)
            .FirstOrDefaultAsync(y => y.Id == id);
    }

    public async Task<BudgetYear?> GetActiveYearAsync()
    {
        var activeYear = await _dbContext.BudgetYears
            .FirstOrDefaultAsync(y => y.Status == BudgetYearStatus.Active);

        if (activeYear is null)
            return null;

        return await GetYearByIdAsync(activeYear.Id);
    }

    public async Task<BudgetYear> CreateYearAsync(string year, string name, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var budgetYear = new BudgetYear
        {
            Id = Guid.NewGuid(),
            Year = year,
            Name = name,
            Status = BudgetYearStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.BudgetYears.Add(budgetYear);

        // Auto-create "Departments" group
        var departmentGroup = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYear.Id,
            Name = "Departments",
            SortOrder = 0,
            IsRestricted = false,
            IsDepartmentGroup = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.BudgetGroups.Add(departmentGroup);

        // Auto-create categories for teams with HasBudget
        var budgetTeams = await _dbContext.Teams
            .Where(t => t.HasBudget && t.IsActive)
            .OrderBy(t => t.Name)
            .ToListAsync();

        var sortOrder = 0;
        foreach (var team in budgetTeams)
        {
            var category = new BudgetCategory
            {
                Id = Guid.NewGuid(),
                BudgetGroupId = departmentGroup.Id,
                Name = team.Name,
                AllocatedAmount = 0,
                ExpenditureType = ExpenditureType.OpEx,
                TeamId = team.Id,
                SortOrder = sortOrder++,
                CreatedAt = now,
                UpdatedAt = now
            };

            _dbContext.BudgetCategories.Add(category);
        }

        LogAudit(budgetYear.Id, nameof(BudgetYear), budgetYear.Id,
            $"Created budget year '{name}' ({year})",
            actorUserId, now);

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created budget year {Year} ({Name}) with {TeamCount} department categories",
            year, name, budgetTeams.Count);

        return budgetYear;
    }

    public async Task UpdateYearStatusAsync(Guid yearId, BudgetYearStatus status, Guid actorUserId)
    {
        var year = await _dbContext.BudgetYears.FindAsync(yearId)
            ?? throw new InvalidOperationException($"Budget year {yearId} not found");

        var now = _clock.GetCurrentInstant();
        var oldStatus = year.Status;

        // When activating, auto-close any currently active year
        if (status == BudgetYearStatus.Active)
        {
            var currentlyActive = await _dbContext.BudgetYears
                .Where(y => y.Status == BudgetYearStatus.Active && y.Id != yearId)
                .ToListAsync();

            foreach (var active in currentlyActive)
            {
                active.Status = BudgetYearStatus.Closed;
                active.UpdatedAt = now;

                LogAudit(active.Id, nameof(BudgetYear), active.Id,
                    nameof(BudgetYear.Status), BudgetYearStatus.Active.ToString(), BudgetYearStatus.Closed.ToString(),
                    actorUserId, now);

                _logger.LogInformation("Auto-closed budget year {YearId} ({Year}) due to activation of {NewYearId}",
                    active.Id, active.Year, yearId);
            }
        }

        year.Status = status;
        year.UpdatedAt = now;

        LogAudit(year.Id, nameof(BudgetYear), year.Id,
            nameof(BudgetYear.Status), oldStatus.ToString(), status.ToString(),
            actorUserId, now);

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated budget year {YearId} status from {OldStatus} to {NewStatus}",
            yearId, oldStatus, status);
    }

    public async Task UpdateYearAsync(Guid yearId, string year, string name, Guid actorUserId)
    {
        var budgetYear = await _dbContext.BudgetYears.FindAsync(yearId)
            ?? throw new InvalidOperationException($"Budget year {yearId} not found");

        var now = _clock.GetCurrentInstant();

        if (!string.Equals(budgetYear.Year, year, StringComparison.Ordinal))
        {
            LogAudit(budgetYear.Id, nameof(BudgetYear), budgetYear.Id,
                nameof(BudgetYear.Year), budgetYear.Year, year,
                actorUserId, now);
            budgetYear.Year = year;
        }

        if (!string.Equals(budgetYear.Name, name, StringComparison.Ordinal))
        {
            LogAudit(budgetYear.Id, nameof(BudgetYear), budgetYear.Id,
                nameof(BudgetYear.Name), budgetYear.Name, name,
                actorUserId, now);
            budgetYear.Name = name;
        }

        budgetYear.UpdatedAt = now;

        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteYearAsync(Guid yearId, Guid actorUserId)
    {
        var year = await _dbContext.BudgetYears.FindAsync(yearId)
            ?? throw new InvalidOperationException($"Budget year {yearId} not found");

        if (year.Status == BudgetYearStatus.Active)
            throw new InvalidOperationException("Cannot delete an active budget year. Close it first.");

        var now = _clock.GetCurrentInstant();

        LogAudit(year.Id, nameof(BudgetYear), year.Id,
            $"Deleted budget year '{year.Name}' ({year.Year})",
            actorUserId, now);

        await _dbContext.SaveChangesAsync();

        _dbContext.BudgetYears.Remove(year);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted budget year {YearId} ({Year})", yearId, year.Year);
    }

    public async Task<int> SyncDepartmentsAsync(Guid budgetYearId, Guid actorUserId)
    {
        await EnsureYearNotClosedAsync(budgetYearId);

        var deptGroup = await _dbContext.BudgetGroups
            .Include(g => g.Categories)
            .FirstOrDefaultAsync(g => g.BudgetYearId == budgetYearId && g.IsDepartmentGroup)
            ?? throw new InvalidOperationException("No Departments group found for this budget year");

        var existingTeamIds = deptGroup.Categories
            .Where(c => c.TeamId.HasValue)
            .Select(c => c.TeamId!.Value)
            .ToHashSet();

        var budgetTeams = await _dbContext.Teams
            .Where(t => t.HasBudget && t.IsActive && !existingTeamIds.Contains(t.Id))
            .OrderBy(t => t.Name)
            .ToListAsync();

        if (budgetTeams.Count == 0)
            return 0;

        var now = _clock.GetCurrentInstant();
        var maxSortOrder = deptGroup.Categories.Any()
            ? deptGroup.Categories.Max(c => c.SortOrder)
            : -1;

        foreach (var team in budgetTeams)
        {
            var category = new BudgetCategory
            {
                Id = Guid.NewGuid(),
                BudgetGroupId = deptGroup.Id,
                Name = team.Name,
                AllocatedAmount = 0,
                ExpenditureType = ExpenditureType.OpEx,
                TeamId = team.Id,
                SortOrder = ++maxSortOrder,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.BudgetCategories.Add(category);

            LogAudit(budgetYearId, nameof(BudgetCategory), category.Id,
                $"Synced department '{team.Name}' into budget",
                actorUserId, now);
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Synced {Count} departments into budget year {YearId}", budgetTeams.Count, budgetYearId);

        return budgetTeams.Count;
    }

    // ───────────────────────── Budget Groups ─────────────────────────

    public async Task<BudgetGroup> CreateGroupAsync(Guid budgetYearId, string name, bool isRestricted, Guid actorUserId)
    {
        await EnsureYearNotClosedAsync(budgetYearId);

        var year = await _dbContext.BudgetYears.FindAsync(budgetYearId)
            ?? throw new InvalidOperationException($"Budget year {budgetYearId} not found");

        var now = _clock.GetCurrentInstant();

        var maxSortOrder = await _dbContext.BudgetGroups
            .Where(g => g.BudgetYearId == budgetYearId)
            .MaxAsync(g => (int?)g.SortOrder) ?? -1;

        var group = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            Name = name,
            SortOrder = maxSortOrder + 1,
            IsRestricted = isRestricted,
            IsDepartmentGroup = false,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.BudgetGroups.Add(group);

        LogAudit(year.Id, nameof(BudgetGroup), group.Id,
            $"Created budget group '{name}'",
            actorUserId, now);

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created budget group '{Name}' in year {YearId}", name, budgetYearId);

        return group;
    }

    public async Task UpdateGroupAsync(Guid groupId, string name, int sortOrder, bool isRestricted, Guid actorUserId)
    {
        var group = await _dbContext.BudgetGroups.FindAsync(groupId)
            ?? throw new InvalidOperationException($"Budget group {groupId} not found");

        await EnsureYearNotClosedAsync(group.BudgetYearId);
        var now = _clock.GetCurrentInstant();

        if (!string.Equals(group.Name, name, StringComparison.Ordinal))
        {
            LogAudit(group.BudgetYearId, nameof(BudgetGroup), group.Id,
                nameof(BudgetGroup.Name), group.Name, name,
                actorUserId, now);
            group.Name = name;
        }

        if (group.SortOrder != sortOrder)
        {
            LogAudit(group.BudgetYearId, nameof(BudgetGroup), group.Id,
                nameof(BudgetGroup.SortOrder), group.SortOrder.ToString(CultureInfo.InvariantCulture), sortOrder.ToString(CultureInfo.InvariantCulture),
                actorUserId, now);
            group.SortOrder = sortOrder;
        }

        if (group.IsRestricted != isRestricted)
        {
            LogAudit(group.BudgetYearId, nameof(BudgetGroup), group.Id,
                nameof(BudgetGroup.IsRestricted), group.IsRestricted.ToString(), isRestricted.ToString(),
                actorUserId, now);
            group.IsRestricted = isRestricted;
        }

        group.UpdatedAt = now;

        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteGroupAsync(Guid groupId, Guid actorUserId)
    {
        var group = await _dbContext.BudgetGroups.FindAsync(groupId)
            ?? throw new InvalidOperationException($"Budget group {groupId} not found");

        await EnsureYearNotClosedAsync(group.BudgetYearId);

        if (group.IsDepartmentGroup)
            throw new InvalidOperationException("Cannot delete the auto-generated Departments group.");

        var now = _clock.GetCurrentInstant();

        LogAudit(group.BudgetYearId, nameof(BudgetGroup), group.Id,
            $"Deleted budget group '{group.Name}'",
            actorUserId, now);

        await _dbContext.SaveChangesAsync();

        _dbContext.BudgetGroups.Remove(group);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted budget group {GroupId} ('{Name}')", groupId, group.Name);
    }

    // ───────────────────────── Budget Categories ─────────────────────────

    public async Task<BudgetCategory?> GetCategoryByIdAsync(Guid id)
    {
        return await _dbContext.BudgetCategories
            .Include(c => c.BudgetGroup)
                .ThenInclude(g => g!.BudgetYear)
            .Include(c => c.Team)
            .Include(c => c.LineItems.OrderBy(li => li.SortOrder))
                .ThenInclude(li => li.ResponsibleTeam)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<BudgetCategory> CreateCategoryAsync(
        Guid budgetGroupId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid? teamId, Guid actorUserId)
    {
        var group = await _dbContext.BudgetGroups.FindAsync(budgetGroupId)
            ?? throw new InvalidOperationException($"Budget group {budgetGroupId} not found");

        await EnsureYearNotClosedAsync(group.BudgetYearId);
        var now = _clock.GetCurrentInstant();

        var maxSortOrder = await _dbContext.BudgetCategories
            .Where(c => c.BudgetGroupId == budgetGroupId)
            .MaxAsync(c => (int?)c.SortOrder) ?? -1;

        var category = new BudgetCategory
        {
            Id = Guid.NewGuid(),
            BudgetGroupId = budgetGroupId,
            Name = name,
            AllocatedAmount = allocatedAmount,
            ExpenditureType = expenditureType,
            TeamId = teamId,
            SortOrder = maxSortOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.BudgetCategories.Add(category);

        LogAudit(group.BudgetYearId, nameof(BudgetCategory), category.Id,
            $"Created budget category '{name}' with allocation {allocatedAmount:N2}",
            actorUserId, now);

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created budget category '{Name}' in group {GroupId}", name, budgetGroupId);

        return category;
    }

    public async Task UpdateCategoryAsync(
        Guid categoryId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid actorUserId)
    {
        var category = await _dbContext.BudgetCategories
            .Include(c => c.BudgetGroup)
            .FirstOrDefaultAsync(c => c.Id == categoryId)
            ?? throw new InvalidOperationException($"Budget category {categoryId} not found");

        var budgetYearId = category.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(budgetYearId);
        var now = _clock.GetCurrentInstant();

        if (!string.Equals(category.Name, name, StringComparison.Ordinal))
        {
            LogAudit(budgetYearId, nameof(BudgetCategory), category.Id,
                nameof(BudgetCategory.Name), category.Name, name,
                actorUserId, now);
            category.Name = name;
        }

        if (category.AllocatedAmount != allocatedAmount)
        {
            LogAudit(budgetYearId, nameof(BudgetCategory), category.Id,
                nameof(BudgetCategory.AllocatedAmount),
                category.AllocatedAmount.ToString("N2", CultureInfo.InvariantCulture), allocatedAmount.ToString("N2", CultureInfo.InvariantCulture),
                actorUserId, now);
            category.AllocatedAmount = allocatedAmount;
        }

        if (category.ExpenditureType != expenditureType)
        {
            LogAudit(budgetYearId, nameof(BudgetCategory), category.Id,
                nameof(BudgetCategory.ExpenditureType),
                category.ExpenditureType.ToString(), expenditureType.ToString(),
                actorUserId, now);
            category.ExpenditureType = expenditureType;
        }

        category.UpdatedAt = now;

        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteCategoryAsync(Guid categoryId, Guid actorUserId)
    {
        var category = await _dbContext.BudgetCategories
            .Include(c => c.BudgetGroup)
            .FirstOrDefaultAsync(c => c.Id == categoryId)
            ?? throw new InvalidOperationException($"Budget category {categoryId} not found");

        var budgetYearId = category.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(budgetYearId);
        var now = _clock.GetCurrentInstant();

        LogAudit(budgetYearId, nameof(BudgetCategory), category.Id,
            $"Deleted budget category '{category.Name}'",
            actorUserId, now);

        await _dbContext.SaveChangesAsync();

        _dbContext.BudgetCategories.Remove(category);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted budget category {CategoryId} ('{Name}')", categoryId, category.Name);
    }

    // ───────────────────────── Budget Line Items ─────────────────────────

    public async Task<BudgetLineItem> CreateLineItemAsync(
        Guid budgetCategoryId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, Guid actorUserId)
    {
        var category = await _dbContext.BudgetCategories
            .Include(c => c.BudgetGroup)
            .FirstOrDefaultAsync(c => c.Id == budgetCategoryId)
            ?? throw new InvalidOperationException($"Budget category {budgetCategoryId} not found");

        var budgetYearId = category.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(budgetYearId);
        var now = _clock.GetCurrentInstant();

        var maxSortOrder = await _dbContext.BudgetLineItems
            .Where(li => li.BudgetCategoryId == budgetCategoryId)
            .MaxAsync(li => (int?)li.SortOrder) ?? -1;

        var lineItem = new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = budgetCategoryId,
            Description = description,
            Amount = amount,
            ResponsibleTeamId = responsibleTeamId,
            Notes = notes,
            ExpectedDate = expectedDate,
            SortOrder = maxSortOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.BudgetLineItems.Add(lineItem);

        LogAudit(budgetYearId, nameof(BudgetLineItem), lineItem.Id,
            $"Created line item '{description}' ({amount:N2})",
            actorUserId, now);

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created line item '{Description}' in category {CategoryId}", description, budgetCategoryId);

        return lineItem;
    }

    public async Task UpdateLineItemAsync(
        Guid lineItemId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, LocalDate? expectedDate, Guid actorUserId)
    {
        var lineItem = await _dbContext.BudgetLineItems
            .Include(li => li.BudgetCategory)
                .ThenInclude(c => c!.BudgetGroup)
            .FirstOrDefaultAsync(li => li.Id == lineItemId)
            ?? throw new InvalidOperationException($"Budget line item {lineItemId} not found");

        var budgetYearId = lineItem.BudgetCategory!.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(budgetYearId);
        var now = _clock.GetCurrentInstant();

        if (!string.Equals(lineItem.Description, description, StringComparison.Ordinal))
        {
            LogAudit(budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.Description), lineItem.Description, description,
                actorUserId, now);
            lineItem.Description = description;
        }

        if (lineItem.Amount != amount)
        {
            LogAudit(budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.Amount),
                lineItem.Amount.ToString("N2", CultureInfo.InvariantCulture), amount.ToString("N2", CultureInfo.InvariantCulture),
                actorUserId, now);
            lineItem.Amount = amount;
        }

        if (lineItem.ResponsibleTeamId != responsibleTeamId)
        {
            LogAudit(budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.ResponsibleTeamId),
                lineItem.ResponsibleTeamId?.ToString(), responsibleTeamId?.ToString(),
                actorUserId, now);
            lineItem.ResponsibleTeamId = responsibleTeamId;
        }

        if (!string.Equals(lineItem.Notes, notes, StringComparison.Ordinal))
        {
            LogAudit(budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.Notes), lineItem.Notes, notes,
                actorUserId, now);
            lineItem.Notes = notes;
        }

        if (lineItem.ExpectedDate != expectedDate)
        {
            LogAudit(budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.ExpectedDate),
                lineItem.ExpectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                expectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                actorUserId, now);
            lineItem.ExpectedDate = expectedDate;
        }

        lineItem.UpdatedAt = now;

        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteLineItemAsync(Guid lineItemId, Guid actorUserId)
    {
        var lineItem = await _dbContext.BudgetLineItems
            .Include(li => li.BudgetCategory)
                .ThenInclude(c => c!.BudgetGroup)
            .FirstOrDefaultAsync(li => li.Id == lineItemId)
            ?? throw new InvalidOperationException($"Budget line item {lineItemId} not found");

        var budgetYearId = lineItem.BudgetCategory!.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(budgetYearId);
        var now = _clock.GetCurrentInstant();

        LogAudit(budgetYearId, nameof(BudgetLineItem), lineItem.Id,
            $"Deleted line item '{lineItem.Description}'",
            actorUserId, now);

        await _dbContext.SaveChangesAsync();

        _dbContext.BudgetLineItems.Remove(lineItem);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted line item {LineItemId} ('{Description}')", lineItemId, lineItem.Description);
    }

    // ───────────────────────── Audit Log ─────────────────────────

    public async Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(Guid? budgetYearId)
    {
        var query = _dbContext.BudgetAuditLogs
            .Include(a => a.ActorUser)
            .AsQueryable();

        if (budgetYearId.HasValue)
            query = query.Where(a => a.BudgetYearId == budgetYearId.Value);

        return await query
            .OrderByDescending(a => a.OccurredAt)
            .Take(500)
            .ToListAsync();
    }

    // ───────────────────────── Private Helpers ─────────────────────────

    private async Task EnsureYearNotClosedAsync(Guid budgetYearId)
    {
        var year = await _dbContext.BudgetYears.FindAsync(budgetYearId);
        if (year?.Status == BudgetYearStatus.Closed)
            throw new InvalidOperationException("Cannot modify a closed budget year.");
    }

    /// <summary>
    /// Logs a field-level change with old/new values and a generated description.
    /// </summary>
    private void LogAudit(
        Guid budgetYearId, string entityType, Guid entityId,
        string fieldName, string? oldValue, string? newValue,
        Guid actorUserId, Instant occurredAt)
    {
        var entry = new BudgetAuditLog
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            EntityType = entityType,
            EntityId = entityId,
            FieldName = fieldName,
            OldValue = oldValue,
            NewValue = newValue,
            Description = $"Changed {entityType}.{fieldName} from '{oldValue}' to '{newValue}'",
            ActorUserId = actorUserId,
            OccurredAt = occurredAt
        };

        _dbContext.BudgetAuditLogs.Add(entry);
    }

    /// <summary>
    /// Logs a create/delete action with a free-text description.
    /// </summary>
    private void LogAudit(
        Guid budgetYearId, string entityType, Guid entityId,
        string description, Guid actorUserId, Instant occurredAt)
    {
        var entry = new BudgetAuditLog
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            EntityType = entityType,
            EntityId = entityId,
            FieldName = null,
            OldValue = null,
            NewValue = null,
            Description = description,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt
        };

        _dbContext.BudgetAuditLogs.Add(entry);
    }
}
