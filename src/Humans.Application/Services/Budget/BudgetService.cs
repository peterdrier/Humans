using System.Globalization;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Budget;

/// <summary>
/// Application-layer implementation of <see cref="IBudgetService"/>. Goes
/// through <see cref="IBudgetRepository"/> for all data access — this type
/// never imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph. Cross-section reads
/// (budget-flagged teams, coordinator team IDs) go through
/// <see cref="ITeamService"/>.
/// </summary>
/// <remarks>
/// <c>budget_audit_logs</c> is append-only by convention — the service only
/// issues <see cref="IBudgetRepository.AddAuditLog"/> calls and never updates
/// or deletes audit rows. Multi-entity operations (create year + seed groups +
/// audit entry) stage writes on the repository and commit them in a single
/// <see cref="IBudgetRepository.SaveChangesAsync"/> call so they are atomic.
/// </remarks>
public sealed class BudgetService : IBudgetService, IUserDataContributor
{
    private readonly IBudgetRepository _repository;
    private readonly ITeamService _teamService;
    private readonly IClock _clock;
    private readonly ILogger<BudgetService> _logger;

    public BudgetService(
        IBudgetRepository repository,
        ITeamService teamService,
        IClock clock,
        ILogger<BudgetService> logger)
    {
        _repository = repository;
        _teamService = teamService;
        _clock = clock;
        _logger = logger;
    }

    // ───────────────────────── Budget Years ─────────────────────────

    public Task<IReadOnlyList<BudgetYear>> GetAllYearsAsync(bool includeArchived = false) =>
        _repository.GetAllYearsAsync(includeArchived);

    public Task<BudgetYear?> GetYearByIdAsync(Guid id) => _repository.GetYearByIdAsync(id);

    public Task<BudgetYear?> GetActiveYearAsync() => _repository.GetActiveYearAsync();

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

        _repository.AddYear(budgetYear);

        // Auto-create "Departments" group.
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

        _repository.AddGroup(departmentGroup);

        // Auto-create categories for teams with HasBudget (via Teams section).
        var budgetTeams = await _teamService.GetBudgetableTeamsAsync();

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

            _repository.AddCategory(category);
        }

        // Auto-create "Ticketing" group with projection defaults.
        var ticketingGroup = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYear.Id,
            Name = "Ticketing",
            SortOrder = 1,
            IsRestricted = false,
            IsTicketingGroup = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _repository.AddGroup(ticketingGroup);

        var ticketingProjection = new TicketingProjection
        {
            Id = Guid.NewGuid(),
            BudgetGroupId = ticketingGroup.Id,
            CreatedAt = now,
            UpdatedAt = now
        };

        _repository.AddTicketingProjection(ticketingProjection);

        var ticketingCategories = new[]
        {
            ("Ticket Revenue", 0),
            ("Processing Fees", 1)
        };

        foreach (var (catName, catSort) in ticketingCategories)
        {
            _repository.AddCategory(new BudgetCategory
            {
                Id = Guid.NewGuid(),
                BudgetGroupId = ticketingGroup.Id,
                Name = catName,
                AllocatedAmount = 0,
                ExpenditureType = ExpenditureType.OpEx,
                SortOrder = catSort,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        LogAudit(budgetYear.Id, nameof(BudgetYear), budgetYear.Id,
            $"Created budget year '{name}' ({year})",
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _logger.LogInformation("Created budget year {Year} ({Name}) with {TeamCount} department categories",
            year, name, budgetTeams.Count);

        return budgetYear;
    }

    public async Task UpdateYearStatusAsync(Guid yearId, BudgetYearStatus status, Guid actorUserId)
    {
        var year = await _repository.FindYearForMutationAsync(yearId)
            ?? throw new InvalidOperationException($"Budget year {yearId} not found");

        var now = _clock.GetCurrentInstant();
        var oldStatus = year.Status;

        // When activating, auto-close any currently active year.
        if (status == BudgetYearStatus.Active)
        {
            var currentlyActive = await _repository.FindActiveYearsExcludingAsync(yearId);

            foreach (var active in currentlyActive)
            {
                active.Status = BudgetYearStatus.Closed;
                active.UpdatedAt = now;

                LogAudit(active.Id, nameof(BudgetYear), active.Id,
                    nameof(BudgetYear.Status), BudgetYearStatus.Active.ToString(), BudgetYearStatus.Closed.ToString(),
                    actorUserId, now);
            }
        }

        year.Status = status;
        year.UpdatedAt = now;

        LogAudit(year.Id, nameof(BudgetYear), year.Id,
            nameof(BudgetYear.Status), oldStatus.ToString(), status.ToString(),
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _logger.LogInformation("Updated budget year {YearId} status from {OldStatus} to {NewStatus}",
            yearId, oldStatus, status);
    }

    public async Task UpdateYearAsync(Guid yearId, string year, string name, Guid actorUserId)
    {
        var budgetYear = await _repository.FindYearForMutationAsync(yearId)
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

        await _repository.SaveChangesAsync();
    }

    public async Task DeleteYearAsync(Guid yearId, Guid actorUserId)
    {
        var year = await _repository.FindYearForMutationAsync(yearId)
            ?? throw new InvalidOperationException($"Budget year {yearId} not found");

        if (year.Status == BudgetYearStatus.Active)
            throw new InvalidOperationException("Cannot delete an active budget year. Close it first.");

        var now = _clock.GetCurrentInstant();

        // Soft-delete: mark as archived, preserve all data and audit logs.
        year.IsDeleted = true;
        year.DeletedAt = now;
        year.Status = BudgetYearStatus.Closed;
        year.UpdatedAt = now;

        LogAudit(year.Id, nameof(BudgetYear), year.Id,
            $"Archived budget year '{year.Name}' ({year.Year})",
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _logger.LogInformation("Archived budget year {YearId} ({Year})", yearId, year.Year);
    }

    public async Task RestoreYearAsync(Guid yearId, Guid actorUserId)
    {
        var year = await _repository.FindYearForMutationAsync(yearId)
            ?? throw new InvalidOperationException($"Budget year {yearId} not found");

        if (!year.IsDeleted)
            return;

        var now = _clock.GetCurrentInstant();

        year.IsDeleted = false;
        year.DeletedAt = null;
        year.Status = BudgetYearStatus.Draft;
        year.UpdatedAt = now;

        LogAudit(year.Id, nameof(BudgetYear), year.Id,
            $"Restored budget year '{year.Name}' ({year.Year})",
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _logger.LogInformation("Restored budget year {YearId} ({Year})", yearId, year.Year);
    }

    public async Task<int> SyncDepartmentsAsync(Guid budgetYearId, Guid actorUserId)
    {
        await EnsureYearNotClosedAsync(budgetYearId);

        var deptGroup = await _repository.GetDepartmentGroupForMutationAsync(budgetYearId)
            ?? throw new InvalidOperationException("No Departments group found for this budget year");

        var existingTeamIds = deptGroup.Categories
            .Where(c => c.TeamId.HasValue)
            .Select(c => c.TeamId!.Value)
            .ToHashSet();

        var budgetableTeams = await _teamService.GetBudgetableTeamsAsync();
        var newTeams = budgetableTeams
            .Where(t => !existingTeamIds.Contains(t.Id))
            .ToList();

        if (newTeams.Count == 0)
            return 0;

        var now = _clock.GetCurrentInstant();
        var maxSortOrder = deptGroup.Categories.Any()
            ? deptGroup.Categories.Max(c => c.SortOrder)
            : -1;

        foreach (var team in newTeams)
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
            _repository.AddCategory(category);

            LogAudit(budgetYearId, nameof(BudgetCategory), category.Id,
                $"Synced department '{team.Name}' into budget",
                actorUserId, now);
        }

        await _repository.SaveChangesAsync();

        _logger.LogInformation("Synced {Count} departments into budget year {YearId}", newTeams.Count, budgetYearId);

        return newTeams.Count;
    }

    public async Task<bool> EnsureTicketingGroupAsync(Guid budgetYearId, Guid actorUserId)
    {
        await EnsureYearNotClosedAsync(budgetYearId);

        if (await _repository.HasTicketingGroupAsync(budgetYearId))
            return false;

        var now = _clock.GetCurrentInstant();

        var maxSortOrder = await _repository.GetMaxGroupSortOrderAsync(budgetYearId);

        var ticketingGroup = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            Name = "Ticketing",
            SortOrder = maxSortOrder + 1,
            IsRestricted = false,
            IsTicketingGroup = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _repository.AddGroup(ticketingGroup);

        var ticketingProjection = new TicketingProjection
        {
            Id = Guid.NewGuid(),
            BudgetGroupId = ticketingGroup.Id,
            CreatedAt = now,
            UpdatedAt = now
        };

        _repository.AddTicketingProjection(ticketingProjection);

        var ticketingCategories = new[]
        {
            ("Ticket Revenue", 0),
            ("Processing Fees", 1)
        };

        foreach (var (catName, catSort) in ticketingCategories)
        {
            _repository.AddCategory(new BudgetCategory
            {
                Id = Guid.NewGuid(),
                BudgetGroupId = ticketingGroup.Id,
                Name = catName,
                AllocatedAmount = 0,
                ExpenditureType = ExpenditureType.OpEx,
                SortOrder = catSort,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        LogAudit(budgetYearId, nameof(BudgetGroup), ticketingGroup.Id,
            "Added Ticketing group with projection parameters",
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _logger.LogInformation("Added ticketing group to budget year {YearId}", budgetYearId);

        return true;
    }

    // ───────────────────────── Budget Groups ─────────────────────────

    public async Task<BudgetGroup> CreateGroupAsync(Guid budgetYearId, string name, bool isRestricted, Guid actorUserId)
    {
        await EnsureYearNotClosedAsync(budgetYearId);

        var year = await _repository.FindYearForMutationAsync(budgetYearId)
            ?? throw new InvalidOperationException($"Budget year {budgetYearId} not found");

        var now = _clock.GetCurrentInstant();

        var maxSortOrder = await _repository.GetMaxGroupSortOrderAsync(budgetYearId);

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

        _repository.AddGroup(group);

        LogAudit(year.Id, nameof(BudgetGroup), group.Id,
            $"Created budget group '{name}'",
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _logger.LogInformation("Created budget group '{Name}' in year {YearId}", name, budgetYearId);

        return group;
    }

    public async Task UpdateGroupAsync(Guid groupId, string name, int sortOrder, bool isRestricted, Guid actorUserId)
    {
        var group = await _repository.FindGroupForMutationAsync(groupId)
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

        await _repository.SaveChangesAsync();
    }

    public async Task DeleteGroupAsync(Guid groupId, Guid actorUserId)
    {
        var group = await _repository.FindGroupForMutationAsync(groupId)
            ?? throw new InvalidOperationException($"Budget group {groupId} not found");

        await EnsureYearNotClosedAsync(group.BudgetYearId);

        if (group.IsDepartmentGroup)
            throw new InvalidOperationException("Cannot delete the auto-generated Departments group.");

        var now = _clock.GetCurrentInstant();

        LogAudit(group.BudgetYearId, nameof(BudgetGroup), group.Id,
            $"Deleted budget group '{group.Name}'",
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _repository.RemoveGroup(group);
        await _repository.SaveChangesAsync();

        _logger.LogInformation("Deleted budget group {GroupId} ('{Name}')", groupId, group.Name);
    }

    // ───────────────────────── Budget Categories ─────────────────────────

    public Task<BudgetCategory?> GetCategoryByIdAsync(Guid id) => _repository.GetCategoryByIdAsync(id);

    public async Task<BudgetCategory> CreateCategoryAsync(
        Guid budgetGroupId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid? teamId, Guid actorUserId)
    {
        var group = await _repository.FindGroupForMutationAsync(budgetGroupId)
            ?? throw new InvalidOperationException($"Budget group {budgetGroupId} not found");

        await EnsureYearNotClosedAsync(group.BudgetYearId);
        var now = _clock.GetCurrentInstant();

        var maxSortOrder = await _repository.GetMaxCategorySortOrderAsync(budgetGroupId);

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

        _repository.AddCategory(category);

        LogAudit(group.BudgetYearId, nameof(BudgetCategory), category.Id,
            $"Created budget category '{name}' with allocation {allocatedAmount:N2}",
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _logger.LogInformation("Created budget category '{Name}' in group {GroupId}", name, budgetGroupId);

        return category;
    }

    public async Task UpdateCategoryAsync(
        Guid categoryId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid actorUserId)
    {
        var category = await _repository.FindCategoryForMutationAsync(categoryId)
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

        await _repository.SaveChangesAsync();
    }

    public async Task DeleteCategoryAsync(Guid categoryId, Guid actorUserId)
    {
        var category = await _repository.FindCategoryForMutationAsync(categoryId)
            ?? throw new InvalidOperationException($"Budget category {categoryId} not found");

        var budgetYearId = category.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(budgetYearId);
        var now = _clock.GetCurrentInstant();

        LogAudit(budgetYearId, nameof(BudgetCategory), category.Id,
            $"Deleted budget category '{category.Name}'",
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _repository.RemoveCategory(category);
        await _repository.SaveChangesAsync();

        _logger.LogInformation("Deleted budget category {CategoryId} ('{Name}')", categoryId, category.Name);
    }

    // ───────────────────────── Budget Line Items ─────────────────────────

    public Task<BudgetLineItem?> GetLineItemByIdAsync(Guid id) => _repository.GetLineItemByIdAsync(id);

    public async Task<BudgetLineItem> CreateLineItemAsync(
        Guid budgetCategoryId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, LocalDate? expectedDate,
        int vatRate, Guid actorUserId)
    {
        ValidateVatRate(vatRate);

        var category = await _repository.FindCategoryForMutationAsync(budgetCategoryId)
            ?? throw new InvalidOperationException($"Budget category {budgetCategoryId} not found");

        var budgetYearId = category.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(budgetYearId);
        var now = _clock.GetCurrentInstant();

        var maxSortOrder = await _repository.GetMaxLineItemSortOrderAsync(budgetCategoryId);

        var lineItem = new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = budgetCategoryId,
            Description = description,
            Amount = amount,
            ResponsibleTeamId = responsibleTeamId,
            Notes = notes,
            ExpectedDate = expectedDate,
            VatRate = vatRate,
            SortOrder = maxSortOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _repository.AddLineItem(lineItem);

        LogAudit(budgetYearId, nameof(BudgetLineItem), lineItem.Id,
            $"Created line item '{description}' ({amount:N2})",
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _logger.LogInformation("Created line item '{Description}' in category {CategoryId}", description, budgetCategoryId);

        return lineItem;
    }

    public async Task UpdateLineItemAsync(
        Guid lineItemId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, LocalDate? expectedDate,
        int vatRate, Guid actorUserId)
    {
        ValidateVatRate(vatRate);

        var lineItem = await _repository.FindLineItemForMutationAsync(lineItemId)
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

        if (lineItem.VatRate != vatRate)
        {
            LogAudit(budgetYearId, nameof(BudgetLineItem), lineItem.Id,
                nameof(BudgetLineItem.VatRate),
                lineItem.VatRate.ToString(CultureInfo.InvariantCulture), vatRate.ToString(CultureInfo.InvariantCulture),
                actorUserId, now);
            lineItem.VatRate = vatRate;
        }

        lineItem.UpdatedAt = now;

        await _repository.SaveChangesAsync();
    }

    private static void ValidateVatRate(int vatRate)
    {
        if (vatRate is < 0 or > 21)
            throw new ArgumentOutOfRangeException(nameof(vatRate), vatRate, "VAT rate must be between 0 and 21.");
    }

    public async Task DeleteLineItemAsync(Guid lineItemId, Guid actorUserId)
    {
        var lineItem = await _repository.FindLineItemForMutationAsync(lineItemId)
            ?? throw new InvalidOperationException($"Budget line item {lineItemId} not found");

        var budgetYearId = lineItem.BudgetCategory!.BudgetGroup!.BudgetYearId;
        await EnsureYearNotClosedAsync(budgetYearId);
        var now = _clock.GetCurrentInstant();

        LogAudit(budgetYearId, nameof(BudgetLineItem), lineItem.Id,
            $"Deleted line item '{lineItem.Description}'",
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _repository.RemoveLineItem(lineItem);
        await _repository.SaveChangesAsync();

        _logger.LogInformation("Deleted line item {LineItemId} ('{Description}')", lineItemId, lineItem.Description);
    }

    // ───────────────────────── Ticketing Projection ─────────────────────────

    public Task<TicketingProjection?> GetTicketingProjectionAsync(Guid budgetGroupId) =>
        _repository.GetTicketingProjectionAsync(budgetGroupId);

    public async Task UpdateTicketingProjectionAsync(
        Guid budgetGroupId, LocalDate? startDate, LocalDate? eventDate,
        int initialSalesCount, decimal dailySalesRate, decimal averageTicketPrice, int vatRate,
        decimal stripeFeePercent, decimal stripeFeeFixed, decimal ticketTailorFeePercent, Guid actorUserId)
    {
        var group = await _repository.FindGroupForMutationAsync(budgetGroupId)
            ?? throw new InvalidOperationException($"Budget group {budgetGroupId} not found");

        if (!group.IsTicketingGroup)
            throw new InvalidOperationException("Projection parameters can only be set on ticketing groups.");

        await EnsureYearNotClosedAsync(group.BudgetYearId);

        var projection = await _repository.FindTicketingProjectionForMutationAsync(budgetGroupId)
            ?? throw new InvalidOperationException("No ticketing projection found for this group.");

        var now = _clock.GetCurrentInstant();

        projection.StartDate = startDate;
        projection.EventDate = eventDate;
        projection.InitialSalesCount = initialSalesCount;
        projection.DailySalesRate = dailySalesRate;
        projection.AverageTicketPrice = averageTicketPrice;
        projection.VatRate = vatRate;
        projection.StripeFeePercent = stripeFeePercent;
        projection.StripeFeeFixed = stripeFeeFixed;
        projection.TicketTailorFeePercent = ticketTailorFeePercent;
        projection.UpdatedAt = now;

        LogAudit(group.BudgetYearId, nameof(TicketingProjection), projection.Id,
            "Updated ticketing projection parameters",
            actorUserId, now);

        await _repository.SaveChangesAsync();

        _logger.LogInformation("Updated ticketing projection for group {GroupId}", budgetGroupId);
    }

    // ───────────────────────── Audit Log ─────────────────────────

    public Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(Guid? budgetYearId) =>
        _repository.GetAuditLogAsync(budgetYearId);

    // ───────────────────────── Private Helpers ─────────────────────────

    private async Task EnsureYearNotClosedAsync(Guid budgetYearId)
    {
        if (await _repository.IsYearClosedAsync(budgetYearId))
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
        var description = $"Changed {entityType}.{fieldName} from '{oldValue}' to '{newValue}'";
        var entry = new BudgetAuditLog
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            EntityType = entityType,
            EntityId = entityId,
            FieldName = fieldName,
            OldValue = oldValue,
            NewValue = newValue,
            Description = description,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt
        };

        _repository.AddAuditLog(entry);

        _logger.LogInformation("BudgetAudit: {EntityType} {EntityId} — {Description} by user {ActorUserId}",
            entityType, entityId, description, actorUserId);
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

        _repository.AddAuditLog(entry);

        _logger.LogInformation("BudgetAudit: {EntityType} {EntityId} — {Description} by user {ActorUserId}",
            entityType, entityId, description, actorUserId);
    }

    // ───────────────────────── Coordinator ─────────────────────────

    public async Task<HashSet<Guid>> GetEffectiveCoordinatorTeamIdsAsync(Guid userId)
    {
        var ids = await _teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(userId);
        return ids.ToHashSet();
    }

    // ───────────────────────── Summary Computation (pure) ─────────────────────────

    public BudgetSummaryResult ComputeBudgetSummary(IEnumerable<BudgetGroup> groups)
    {
        var budgetLineItems = groups
            .SelectMany(g => g.Categories)
            .SelectMany(c => c.LineItems)
            .Where(li => !li.IsCashflowOnly)
            .ToList();

        // Compute VAT projections.
        var vatProjections = budgetLineItems
            .Where(li => li.VatRate > 0 && li.ExpectedDate.HasValue)
            .Select(li => new
            {
                VatAmount = Math.Abs(li.Amount) * li.VatRate / (100m + li.VatRate),
                IsExpense = li.Amount > 0 // Income generates VAT liability (expense).
            })
            .ToList();

        var income = budgetLineItems.Where(li => li.Amount > 0).Sum(li => li.Amount);
        var expenses = budgetLineItems.Where(li => li.Amount < 0).Sum(li => li.Amount);
        var vatExpenses = vatProjections.Where(v => v.IsExpense).Sum(v => v.VatAmount);
        var vatCredits = vatProjections.Where(v => !v.IsExpense).Sum(v => v.VatAmount);

        var totalIncome = income + vatCredits;
        var totalExpenses = expenses - vatExpenses;
        var netBalance = totalIncome + totalExpenses;

        // Build income slices.
        var incomeCategories = groups
            .SelectMany(g => g.Categories)
            .Select(c => new { c.Name, Total = c.LineItems.Where(li => li.Amount > 0 && !li.IsCashflowOnly).Sum(li => li.Amount) })
            .Where(c => c.Total > 0)
            .OrderByDescending(c => c.Total)
            .ToList();

        if (vatCredits > 0)
            incomeCategories.Add(new { Name = "VAT Credits", Total = vatCredits });

        var totalIncomeForSlices = incomeCategories.Sum(c => c.Total);
        var incomeSlices = incomeCategories
            .Select(c => new BudgetSliceResult
            {
                Name = c.Name,
                Amount = c.Total,
                Percentage = totalIncomeForSlices > 0 ? c.Total / totalIncomeForSlices * 100 : 0
            })
            .ToList();

        // Build expense slices.
        var expenseCategories = groups
            .SelectMany(g => g.Categories)
            .Select(c => new { c.Name, Total = Math.Abs(c.LineItems.Where(li => li.Amount < 0 && !li.IsCashflowOnly).Sum(li => li.Amount)) })
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
            .Select(c => new BudgetSliceResult
            {
                Name = c.Name,
                Amount = c.Total,
                Percentage = totalExpenseForSlices > 0 ? c.Total / totalExpenseForSlices * 100 : 0
            })
            .ToList();

        return new BudgetSummaryResult
        {
            TotalIncome = totalIncome,
            TotalExpenses = totalExpenses,
            NetBalance = netBalance,
            IncomeSlices = incomeSlices,
            ExpenseSlices = expenseSlices
        };
    }

    public BudgetSummaryResult ComputeBudgetSummaryWithBuffers(IEnumerable<BudgetGroup> groups)
    {
        var groupList = groups.ToList();
        var summary = ComputeBudgetSummary(groupList);

        // Per-group buffer: allocated minus line-item total.
        // Negative = expense buffer, positive = income buffer.
        var groupBuffers = groupList
            .Where(g => !g.IsTicketingGroup)
            .Select(g => new
            {
                Name = $"{g.Name} Buffer",
                Raw = g.Categories.Sum(c =>
                    c.AllocatedAmount - c.LineItems.Where(li => !li.IsCashflowOnly).Sum(li => li.Amount))
            })
            .Where(b => b.Raw != 0)
            .ToList();

        var allExpenseEntries = summary.ExpenseSlices
            .Select(s => new { s.Name, s.Amount })
            .Concat(groupBuffers.Where(b => b.Raw < 0).Select(b => new { b.Name, Amount = Math.Abs(b.Raw) }))
            .ToList();
        var totalExpense = allExpenseEntries.Sum(s => s.Amount);

        var allIncomeEntries = summary.IncomeSlices
            .Select(s => new { s.Name, s.Amount })
            .Concat(groupBuffers.Where(b => b.Raw > 0).Select(b => new { b.Name, Amount = b.Raw }))
            .ToList();
        var totalIncome = allIncomeEntries.Sum(s => s.Amount);

        return new BudgetSummaryResult
        {
            TotalIncome = summary.TotalIncome,
            TotalExpenses = summary.TotalExpenses,
            NetBalance = summary.NetBalance,
            IncomeSlices = allIncomeEntries
                .Select(s => new BudgetSliceResult
                {
                    Name = s.Name,
                    Amount = s.Amount,
                    Percentage = totalIncome > 0 ? s.Amount / totalIncome * 100 : 0
                })
                .ToList(),
            ExpenseSlices = allExpenseEntries
                .Select(s => new BudgetSliceResult
                {
                    Name = s.Name,
                    Amount = s.Amount,
                    Percentage = totalExpense > 0 ? s.Amount / totalExpense * 100 : 0
                })
                .ToList()
        };
    }

    public IReadOnlyList<VatCashFlowEntry> ComputeVatCashFlowEntries(IEnumerable<BudgetGroup> groups)
    {
        return groups
            .SelectMany(g => g.Categories)
            .SelectMany(c => c.LineItems)
            .Where(li => li.VatRate > 0 && li.ExpectedDate.HasValue)
            .Select(li =>
            {
                var vatAmount = Math.Abs(li.Amount) * li.VatRate / (100m + li.VatRate);
                // Income → VAT liability (expense); expense → VAT credit (income).
                var cashFlowAmount = li.Amount > 0 ? -vatAmount : vatAmount;
                var categoryName = li.Amount > 0 ? "VAT Liability" : "VAT Credits";
                return new VatCashFlowEntry
                {
                    CategoryName = categoryName,
                    Amount = cashFlowAmount,
                    SettlementDate = ComputeVatSettlementDate(li.ExpectedDate!.Value)
                };
            })
            .ToList();
    }

    public LocalDate ComputeVatSettlementDate(LocalDate expectedDate)
    {
        var quarterEnd = expectedDate.Month switch
        {
            >= 1 and <= 3 => new LocalDate(expectedDate.Year, 3, 31),
            >= 4 and <= 6 => new LocalDate(expectedDate.Year, 6, 30),
            >= 7 and <= 9 => new LocalDate(expectedDate.Year, 9, 30),
            _ => new LocalDate(expectedDate.Year, 12, 31)
        };

        return quarterEnd.PlusDays(45);
    }

    // ───────────────────────── Ticketing Budget Sync ─────────────────────────
    //
    // These methods own the BudgetLineItem / TicketingProjection mutations that
    // used to live in TicketingBudgetService. The ticket side is responsible for
    // aggregating ticket sales per ISO week and passing them in via
    // TicketingWeeklyActuals — the actual upserts, projection-parameter updates,
    // and projected-line materialization happen here because BudgetService owns
    // all budget tables.

    // Prefix for auto-generated line item descriptions to identify them during sync.
    private const string TicketingRevenuePrefix = "Week of ";
    private const string TicketingStripePrefix = "Stripe fees: ";
    private const string TicketingTtPrefix = "TT fees: ";
    private const string TicketingProjectedPrefix = "Projected: ";

    // Spanish IVA rate applied to Stripe and TicketTailor processing fees.
    private const int TicketingFeeVatRate = 21;

    public async Task<int> SyncTicketingActualsAsync(
        Guid budgetYearId,
        IReadOnlyList<TicketingWeeklyActuals> weeklyActuals,
        CancellationToken ct = default)
    {
        var ticketingGroup = await _repository.GetTicketingGroupForMutationAsync(budgetYearId, ct);
        if (ticketingGroup is null)
        {
            _logger.LogDebug("No ticketing group found for budget year {YearId}", budgetYearId);
            return 0;
        }

        var revenueCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "Ticket Revenue", StringComparison.Ordinal));
        var feesCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "Processing Fees", StringComparison.Ordinal));

        if (revenueCategory is null || feesCategory is null)
        {
            _logger.LogWarning("Ticketing group missing expected categories for year {YearId}", budgetYearId);
            return 0;
        }

        var projectionVatRate = ticketingGroup.TicketingProjection?.VatRate ?? 0;
        var now = _clock.GetCurrentInstant();
        var lineItemsCreated = 0;

        foreach (var week in weeklyActuals)
        {
            lineItemsCreated += UpsertTicketingLineItem(revenueCategory,
                $"{TicketingRevenuePrefix}{week.WeekLabel}",
                week.Revenue, week.Monday, projectionVatRate, false, $"{week.TicketCount} tickets", now);

            if (week.StripeFees > 0)
                lineItemsCreated += UpsertTicketingLineItem(feesCategory,
                    $"{TicketingStripePrefix}{week.WeekLabel}",
                    -week.StripeFees, week.Monday, TicketingFeeVatRate, false, null, now);

            if (week.TicketTailorFees > 0)
                lineItemsCreated += UpsertTicketingLineItem(feesCategory,
                    $"{TicketingTtPrefix}{week.WeekLabel}",
                    -week.TicketTailorFees, week.Monday, TicketingFeeVatRate, false, null, now);
        }

        if (weeklyActuals.Count > 0 && ticketingGroup.TicketingProjection is not null)
        {
            var totalRevenue = weeklyActuals.Sum(w => w.Revenue);
            var totalStripeFees = weeklyActuals.Sum(w => w.StripeFees);
            var totalTtFees = weeklyActuals.Sum(w => w.TicketTailorFees);
            var totalTickets = weeklyActuals.Sum(w => w.TicketCount);

            UpdateProjectionFromActuals(ticketingGroup.TicketingProjection,
                totalRevenue, totalStripeFees, totalTtFees, totalTickets, now);
        }

        lineItemsCreated += MaterializeTicketingProjections(ticketingGroup, revenueCategory, feesCategory, now);

        if (_repository.HasPendingChanges())
            await _repository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Ticketing budget sync: {Created} line items created/updated for {Weeks} actual weeks + projections",
            lineItemsCreated, weeklyActuals.Count);

        return lineItemsCreated;
    }

    public async Task<int> RefreshTicketingProjectionsAsync(Guid budgetYearId, CancellationToken ct = default)
    {
        var ticketingGroup = await _repository.GetTicketingGroupForMutationAsync(budgetYearId, ct);
        if (ticketingGroup is null) return 0;

        var revenueCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "Ticket Revenue", StringComparison.Ordinal));
        var feesCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "Processing Fees", StringComparison.Ordinal));
        if (revenueCategory is null || feesCategory is null) return 0;

        var now = _clock.GetCurrentInstant();
        var created = MaterializeTicketingProjections(ticketingGroup, revenueCategory, feesCategory, now);

        if (_repository.HasPendingChanges())
            await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Ticketing projections refreshed: {Count} line items", created);
        return created;
    }

    public async Task<IReadOnlyList<TicketingWeekProjection>> GetTicketingProjectionEntriesAsync(
        Guid budgetGroupId, CancellationToken ct = default)
    {
        // This reads the group graph plus projection. We use the ticketing-group
        // loader but it filters by year — here we need the group by id. Use the
        // projection lookup + group info. Since this is a read-only display path,
        // compute virtual entries entirely in memory from the projection.
        var group = await _repository.FindGroupForMutationAsync(budgetGroupId);
        if (group is null || !group.IsTicketingGroup)
            return [];

        var projection = await _repository.GetTicketingProjectionAsync(budgetGroupId, ct);
        if (projection is null)
            return [];

        if (projection.StartDate is null || projection.EventDate is null || projection.AverageTicketPrice == 0)
            return [];

        var today = _clock.GetCurrentInstant().InUtc().Date;
        var currentWeekMonday = GetTicketingIsoMonday(today);

        var projectionStart = currentWeekMonday > projection.StartDate.Value
            ? currentWeekMonday
            : GetTicketingIsoMonday(projection.StartDate.Value);
        var eventDate = projection.EventDate.Value;

        if (projectionStart >= eventDate)
            return [];

        var dailyRate = projection.DailySalesRate;
        var initialBurst = projection.InitialSalesCount;
        var isFirstWeek = true;

        var projections = new List<TicketingWeekProjection>();
        var weekStart = projectionStart;

        while (weekStart < eventDate)
        {
            var weekEnd = weekStart.PlusDays(6);
            if (weekEnd > eventDate) weekEnd = eventDate;

            var daysInWeek = Period.Between(weekStart, weekEnd.PlusDays(1), PeriodUnits.Days).Days;
            var weekTickets = (int)Math.Round(dailyRate * daysInWeek);
            if (isFirstWeek && projectionStart <= projection.StartDate.Value)
            {
                weekTickets += initialBurst;
                isFirstWeek = false;
            }
            else
            {
                isFirstWeek = false;
            }
            if (weekTickets <= 0) weekTickets = 1;

            var weekRevenue = weekTickets * projection.AverageTicketPrice;
            var stripeFees = weekRevenue * projection.StripeFeePercent / 100m +
                             weekTickets * projection.StripeFeeFixed;
            var ttFees = weekRevenue * projection.TicketTailorFeePercent / 100m;

            projections.Add(new TicketingWeekProjection
            {
                WeekLabel = FormatTicketingWeekLabel(weekStart, weekEnd),
                WeekStart = weekStart,
                WeekEnd = weekEnd,
                ProjectedTickets = weekTickets,
                ProjectedRevenue = Math.Round(weekRevenue, 2),
                ProjectedStripeFees = Math.Round(stripeFees, 2),
                ProjectedTtFees = Math.Round(ttFees, 2)
            });

            weekStart = weekEnd.PlusDays(1);
            // Snap to next Monday.
            weekStart = GetTicketingIsoMonday(weekStart);
            if (weekStart <= weekEnd) weekStart = weekEnd.PlusDays(1);
        }

        return projections;
    }

    public int GetActualTicketsSold(BudgetGroup ticketingGroup)
    {
        var revenueCategory = ticketingGroup.Categories
            .FirstOrDefault(c => string.Equals(c.Name, "Ticket Revenue", StringComparison.Ordinal));

        if (revenueCategory is null) return 0;

        // Sum ticket counts from auto-generated (non-projected) revenue line items.
        // These are the actuals lines with notes like "187 tickets".
        var total = 0;
        foreach (var item in revenueCategory.LineItems)
        {
            if (!item.IsAutoGenerated) continue;
            if (item.Description.StartsWith(TicketingProjectedPrefix, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(item.Notes)) continue;

            // Notes format: "187 tickets" or "~42 tickets" (projected use ~).
            var notes = item.Notes.TrimStart('~');
            var spaceIdx = notes.IndexOf(' ', StringComparison.Ordinal);
            if (spaceIdx > 0 && int.TryParse(
                notes.AsSpan(0, spaceIdx),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count))
            {
                total += count;
            }
        }

        return total;
    }

    /// <summary>
    /// Updates projection parameters (AvgTicketPrice, StripeFeePercent, TicketTailorFeePercent)
    /// from actual order data so that future projections use real averages.
    /// </summary>
    private void UpdateProjectionFromActuals(
        TicketingProjection projection,
        decimal totalRevenue, decimal totalStripeFees, decimal totalTtFees, int totalTickets, Instant now)
    {
        if (totalTickets > 0)
        {
            projection.AverageTicketPrice = Math.Round(totalRevenue / totalTickets, 2);
        }

        if (totalRevenue > 0)
        {
            projection.StripeFeePercent = Math.Round(totalStripeFees / totalRevenue * 100m, 2);
            projection.TicketTailorFeePercent = Math.Round(totalTtFees / totalRevenue * 100m, 2);
        }

        projection.UpdatedAt = now;

        _logger.LogInformation(
            "Updated projection from actuals: AvgPrice={AvgPrice}, StripeFee={StripeFee}%, TtFee={TtFee}%, from {Tickets} tickets",
            projection.AverageTicketPrice, projection.StripeFeePercent, projection.TicketTailorFeePercent, totalTickets);
    }

    /// <summary>
    /// Remove old projected line items, then create new ones from current projection parameters.
    /// Returns number of items created.
    /// </summary>
    private int MaterializeTicketingProjections(
        BudgetGroup ticketingGroup,
        BudgetCategory revenueCategory, BudgetCategory feesCategory, Instant now)
    {
        var projection = ticketingGroup.TicketingProjection;
        if (projection is null || projection.StartDate is null || projection.EventDate is null
            || projection.AverageTicketPrice == 0)
        {
            // No projection configured — remove any stale projected items.
            RemoveTicketingProjectedItems(revenueCategory, feesCategory);
            return 0;
        }

        // Remove old projected items first.
        RemoveTicketingProjectedItems(revenueCategory, feesCategory);

        var today = _clock.GetCurrentInstant().InUtc().Date;
        var currentWeekMonday = GetTicketingIsoMonday(today);
        var eventDate = projection.EventDate.Value;

        var projectionStart = currentWeekMonday > projection.StartDate.Value
            ? currentWeekMonday
            : GetTicketingIsoMonday(projection.StartDate.Value);

        if (projectionStart >= eventDate) return 0;

        var dailyRate = projection.DailySalesRate;
        var initialBurst = projection.InitialSalesCount;
        var isFirstWeek = true;
        var created = 0;
        var weekStart = projectionStart;

        while (weekStart < eventDate)
        {
            var weekEnd = weekStart.PlusDays(6);
            if (weekEnd > eventDate) weekEnd = eventDate;

            var daysInWeek = Period.Between(weekStart, weekEnd.PlusDays(1), PeriodUnits.Days).Days;

            // First projected week includes initial burst if start date hasn't passed.
            var weekTickets = (int)Math.Round(dailyRate * daysInWeek);
            if (isFirstWeek && projectionStart <= projection.StartDate.Value)
            {
                weekTickets += initialBurst;
                isFirstWeek = false;
            }
            else
            {
                isFirstWeek = false;
            }

            if (weekTickets <= 0) weekTickets = 1;

            var weekRevenue = weekTickets * projection.AverageTicketPrice;

            // Fees on revenue only.
            var stripeFees = weekRevenue * projection.StripeFeePercent / 100m +
                             weekTickets * projection.StripeFeeFixed;
            var ttFees = weekRevenue * projection.TicketTailorFeePercent / 100m;

            var weekLabel = FormatTicketingWeekLabel(weekStart, weekEnd);

            // Revenue with VatRate — existing VAT projection system handles VAT automatically.
            created += UpsertTicketingLineItem(revenueCategory, $"{TicketingProjectedPrefix}{TicketingRevenuePrefix}{weekLabel}",
                Math.Round(weekRevenue, 2), weekStart, projection.VatRate, false, $"~{weekTickets} tickets", now);

            if (stripeFees > 0)
                created += UpsertTicketingLineItem(feesCategory, $"{TicketingProjectedPrefix}{TicketingStripePrefix}{weekLabel}",
                    -Math.Round(stripeFees, 2), weekStart, TicketingFeeVatRate, false, null, now);
            if (ttFees > 0)
                created += UpsertTicketingLineItem(feesCategory, $"{TicketingProjectedPrefix}{TicketingTtPrefix}{weekLabel}",
                    -Math.Round(ttFees, 2), weekStart, TicketingFeeVatRate, false, null, now);

            weekStart = weekEnd.PlusDays(1);
            weekStart = GetTicketingIsoMonday(weekStart);
            if (weekStart <= weekEnd) weekStart = weekEnd.PlusDays(1);
        }

        return created;
    }

    private void RemoveTicketingProjectedItems(params BudgetCategory[] categories)
    {
        foreach (var category in categories)
        {
            var projected = category.LineItems
                .Where(li => li.IsAutoGenerated && li.Description.StartsWith(TicketingProjectedPrefix, StringComparison.Ordinal))
                .ToList();

            foreach (var item in projected)
            {
                category.LineItems.Remove(item);
                _repository.RemoveLineItem(item);
            }
        }
    }

    /// <summary>
    /// Upsert a line item by description match within a category (auto-generated items only).
    /// Returns 1 if created or updated, 0 if unchanged.
    /// </summary>
    private int UpsertTicketingLineItem(
        BudgetCategory category, string description, decimal amount,
        LocalDate expectedDate, int vatRate, bool isCashflowOnly, string? notes, Instant now)
    {
        var existing = category.LineItems
            .FirstOrDefault(li => li.IsAutoGenerated && string.Equals(li.Description, description, StringComparison.Ordinal));

        if (existing is not null)
        {
            // Update if values changed.
            if (existing.Amount == amount && existing.VatRate == vatRate
                && string.Equals(existing.Notes, notes, StringComparison.Ordinal))
                return 0;

            existing.Amount = amount;
            existing.VatRate = vatRate;
            existing.Notes = notes;
            existing.ExpectedDate = expectedDate;
            existing.UpdatedAt = now;
            return 1;
        }

        // Create new.
        var maxSort = category.LineItems.Any() ? category.LineItems.Max(li => li.SortOrder) : -1;
        var lineItem = new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = category.Id,
            Description = description,
            Amount = amount,
            ExpectedDate = expectedDate,
            VatRate = vatRate,
            IsAutoGenerated = true,
            IsCashflowOnly = isCashflowOnly,
            Notes = notes,
            SortOrder = maxSort + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _repository.AddLineItem(lineItem);
        category.LineItems.Add(lineItem);
        return 1;
    }

    private static LocalDate GetTicketingIsoMonday(LocalDate date)
    {
        // NodaTime IsoDayOfWeek: Monday=1, Sunday=7.
        var dayOfWeek = (int)date.DayOfWeek;
        return date.PlusDays(-(dayOfWeek - 1));
    }

    private static string FormatTicketingWeekLabel(LocalDate monday, LocalDate sunday)
    {
        return $"{monday.ToString("MMM d", null)}–{sunday.ToString("MMM d", null)}";
    }

    // ───────────────────────── GDPR Export ─────────────────────────

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var entries = await _repository.GetAuditLogEntriesForUserAsync(userId, ct);

        var shaped = entries.Select(bal => new
        {
            bal.EntityType,
            bal.FieldName,
            bal.Description,
            OccurredAt = bal.OccurredAt.ToInvariantInstantString()
        }).ToList();

        return [new UserDataSlice(GdprExportSections.BudgetAuditLog, shaped)];
    }
}
