using System.Globalization;
using Humans.Application.DTOs;
using Humans.Application.DTOs.Finance;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
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
/// requests audit writes through atomic repository methods and never updates
/// or deletes audit rows. Per design-rules §15b, the repository now exposes
/// atomic per-method operations (each opens its own short-lived
/// <c>DbContext</c>). This service therefore does not hold or pass tracked
/// entities between calls — mutations happen inside the repository as part of
/// a single <c>SaveChanges</c>, and composite operations (e.g., creating a
/// year plus its default scaffolding) are single high-level repository
/// methods that do all the work in one transaction.
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

        // Resolve budgetable teams via the Teams section before calling the
        // repository — the repository never crosses the Teams section's
        // ownership boundary (design-rules §2c).
        var teams = await _teamService.GetBudgetableTeamsAsync();
        var teamRefs = teams
            .Select(t => new BudgetableTeamRef(t.Id, t.Name))
            .ToList();

        var draft = new BudgetYearDraft(
            Id: Guid.NewGuid(),
            Year: year,
            Name: name,
            BudgetableTeams: teamRefs,
            ActorUserId: actorUserId,
            Now: now);

        await _repository.CreateYearWithScaffoldAsync(draft);

        _logger.LogInformation(
            "Created budget year {Year} ({Name}) with {TeamCount} department categories",
            year, name, teamRefs.Count);

        return new BudgetYear
        {
            Id = draft.Id,
            Year = year,
            Name = name,
            Status = BudgetYearStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public async Task UpdateYearStatusAsync(Guid yearId, BudgetYearStatus status, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var ok = await _repository.UpdateYearStatusAsync(yearId, status, actorUserId, now);
        if (!ok)
            throw new InvalidOperationException($"Budget year {yearId} not found");

        _logger.LogInformation(
            "Updated budget year {YearId} status to {NewStatus}",
            yearId, status);
    }

    public async Task UpdateYearAsync(Guid yearId, string year, string name, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var ok = await _repository.UpdateYearAsync(yearId, year, name, actorUserId, now);
        if (!ok)
            throw new InvalidOperationException($"Budget year {yearId} not found");
    }

    public async Task DeleteYearAsync(Guid yearId, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var ok = await _repository.SoftDeleteYearAsync(yearId, actorUserId, now);
        if (!ok)
            throw new InvalidOperationException($"Budget year {yearId} not found");

        _logger.LogInformation("Archived budget year {YearId}", yearId);
    }

    public async Task RestoreYearAsync(Guid yearId, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var restored = await _repository.RestoreYearAsync(yearId, actorUserId, now);
        if (restored)
            _logger.LogInformation("Restored budget year {YearId}", yearId);
    }

    public async Task<int> SyncDepartmentsAsync(Guid budgetYearId, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var teams = await _teamService.GetBudgetableTeamsAsync();
        var teamRefs = teams
            .Select(t => new BudgetableTeamRef(t.Id, t.Name))
            .ToList();

        var created = await _repository.SyncDepartmentCategoriesAsync(
            budgetYearId, teamRefs, actorUserId, now);

        if (created > 0)
            _logger.LogInformation(
                "Synced {Count} departments into budget year {YearId}",
                created, budgetYearId);

        return created;
    }

    public async Task<bool> EnsureTicketingGroupAsync(Guid budgetYearId, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var created = await _repository.EnsureTicketingGroupAsync(budgetYearId, actorUserId, now);

        if (created)
            _logger.LogInformation("Added ticketing group to budget year {YearId}", budgetYearId);

        return created;
    }

    // ───────────────────────── Budget Groups ─────────────────────────

    public async Task<BudgetGroup> CreateGroupAsync(
        Guid budgetYearId, string name, bool isRestricted, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var group = await _repository.CreateGroupAsync(
            budgetYearId, name, isRestricted, actorUserId, now);

        _logger.LogInformation(
            "Created budget group '{Name}' in year {YearId}", name, budgetYearId);

        return group;
    }

    public async Task UpdateGroupAsync(
        Guid groupId, string name, int sortOrder, bool isRestricted, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var ok = await _repository.UpdateGroupAsync(
            groupId, name, sortOrder, isRestricted, actorUserId, now);
        if (!ok)
            throw new InvalidOperationException($"Budget group {groupId} not found");
    }

    public async Task DeleteGroupAsync(Guid groupId, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var ok = await _repository.DeleteGroupAsync(groupId, actorUserId, now);
        if (!ok)
            throw new InvalidOperationException($"Budget group {groupId} not found");

        _logger.LogInformation("Deleted budget group {GroupId}", groupId);
    }

    // ───────────────────────── Budget Categories ─────────────────────────

    public Task<BudgetCategory?> GetCategoryByIdAsync(Guid id) => _repository.GetCategoryByIdAsync(id);

    public async Task<BudgetCategory> CreateCategoryAsync(
        Guid budgetGroupId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid? teamId, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var category = await _repository.CreateCategoryAsync(
            budgetGroupId, name, allocatedAmount, expenditureType, teamId, actorUserId, now);

        _logger.LogInformation(
            "Created budget category '{Name}' in group {GroupId}", name, budgetGroupId);

        return category;
    }

    public async Task UpdateCategoryAsync(
        Guid categoryId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var ok = await _repository.UpdateCategoryAsync(
            categoryId, name, allocatedAmount, expenditureType, actorUserId, now);
        if (!ok)
            throw new InvalidOperationException($"Budget category {categoryId} not found");
    }

    public async Task DeleteCategoryAsync(Guid categoryId, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var ok = await _repository.DeleteCategoryAsync(categoryId, actorUserId, now);
        if (!ok)
            throw new InvalidOperationException($"Budget category {categoryId} not found");

        _logger.LogInformation("Deleted budget category {CategoryId}", categoryId);
    }

    // ───────────────────────── Finance Read Surface ─────────────────────────

    public Task<BudgetCategory?> GetCategoryBySlugAsync(
        Guid budgetYearId, string groupSlug, string categorySlug, CancellationToken ct = default)
        => _repository.GetCategoryBySlugAsync(budgetYearId, groupSlug, categorySlug, ct);

    public Task<BudgetYear?> GetYearForDateAsync(LocalDate date, CancellationToken ct = default)
        => _repository.GetYearForDateAsync(date, ct);

    public Task<IReadOnlyList<BudgetCategory>> GetCategoriesByYearAsync(
        Guid budgetYearId, CancellationToken ct = default)
        => _repository.GetCategoriesByYearAsync(budgetYearId, ct);

    public async Task<IReadOnlyList<HoldedTagInventoryRow>> GetTagInventoryAsync(
        Guid budgetYearId, CancellationToken ct = default)
    {
        var rows = await _repository.GetTagInventoryRowsAsync(budgetYearId, ct);
        return rows.Select(r => new HoldedTagInventoryRow(
            BudgetYearId: r.Year.Id,
            Year: r.Year.Year,
            BudgetGroupId: r.Group.Id,
            GroupName: r.Group.Name,
            GroupSlug: r.Group.Slug,
            BudgetCategoryId: r.Category.Id,
            CategoryName: r.Category.Name,
            CategorySlug: r.Category.Slug,
            Tag: $"{r.Group.Slug}-{r.Category.Slug}"
        )).ToList();
    }

    // ───────────────────────── Budget Line Items ─────────────────────────

    public Task<BudgetLineItem?> GetLineItemByIdAsync(Guid id) => _repository.GetLineItemByIdAsync(id);

    public async Task<BudgetLineItem> CreateLineItemAsync(
        Guid budgetCategoryId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, LocalDate? expectedDate,
        int vatRate, Guid actorUserId)
    {
        ValidateVatRate(vatRate);

        var now = _clock.GetCurrentInstant();

        var draft = new BudgetLineItemDraft(
            BudgetCategoryId: budgetCategoryId,
            Description: description,
            Amount: amount,
            ResponsibleTeamId: responsibleTeamId,
            Notes: notes,
            ExpectedDate: expectedDate,
            VatRate: vatRate);

        var lineItem = await _repository.CreateLineItemAsync(draft, actorUserId, now);

        _logger.LogInformation(
            "Created line item '{Description}' in category {CategoryId}",
            description, budgetCategoryId);

        return lineItem;
    }

    public async Task UpdateLineItemAsync(
        Guid lineItemId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, LocalDate? expectedDate,
        int vatRate, Guid actorUserId)
    {
        ValidateVatRate(vatRate);

        var now = _clock.GetCurrentInstant();

        var update = new BudgetLineItemUpdate(
            LineItemId: lineItemId,
            Description: description,
            Amount: amount,
            ResponsibleTeamId: responsibleTeamId,
            Notes: notes,
            ExpectedDate: expectedDate,
            VatRate: vatRate);

        var ok = await _repository.UpdateLineItemAsync(update, actorUserId, now);
        if (!ok)
            throw new InvalidOperationException($"Budget line item {lineItemId} not found");
    }

    private static void ValidateVatRate(int vatRate)
    {
        if (vatRate is < 0 or > 21)
            throw new ArgumentOutOfRangeException(nameof(vatRate), vatRate, "VAT rate must be between 0 and 21.");
    }

    public async Task DeleteLineItemAsync(Guid lineItemId, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var ok = await _repository.DeleteLineItemAsync(lineItemId, actorUserId, now);
        if (!ok)
            throw new InvalidOperationException($"Budget line item {lineItemId} not found");

        _logger.LogInformation("Deleted line item {LineItemId}", lineItemId);
    }

    // ───────────────────────── Ticketing Projection ─────────────────────────

    public Task<TicketingProjection?> GetTicketingProjectionAsync(Guid budgetGroupId) =>
        _repository.GetTicketingProjectionAsync(budgetGroupId);

    public async Task UpdateTicketingProjectionAsync(
        Guid budgetGroupId, LocalDate? startDate, LocalDate? eventDate,
        int initialSalesCount, decimal dailySalesRate, decimal averageTicketPrice, int vatRate,
        decimal stripeFeePercent, decimal stripeFeeFixed, decimal ticketTailorFeePercent, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var update = new TicketingProjectionUpdate(
            BudgetGroupId: budgetGroupId,
            StartDate: startDate,
            EventDate: eventDate,
            InitialSalesCount: initialSalesCount,
            DailySalesRate: dailySalesRate,
            AverageTicketPrice: averageTicketPrice,
            VatRate: vatRate,
            StripeFeePercent: stripeFeePercent,
            StripeFeeFixed: stripeFeeFixed,
            TicketTailorFeePercent: ticketTailorFeePercent);

        await _repository.UpdateTicketingProjectionAsync(update, actorUserId, now);

        _logger.LogInformation("Updated ticketing projection for group {GroupId}", budgetGroupId);
    }

    // ───────────────────────── Audit Log ─────────────────────────

    public Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(Guid? budgetYearId) =>
        _repository.GetAuditLogAsync(budgetYearId);

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
    // The service shapes DTOs and hands them to the repository. Projected-week
    // materialization lives inside the repository so it runs AFTER projection
    // parameters have been refreshed from actuals (same DbContext/SaveChanges),
    // preventing the projected-items-lag-one-sync bug the pre-plan design had.

    public async Task<int> SyncTicketingActualsAsync(
        Guid budgetYearId,
        IReadOnlyList<TicketingWeeklyActuals> weeklyActuals,
        CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var today = now.InUtc().Date;

        var actualsInputs = weeklyActuals
            .Select(w => new TicketingWeeklyActualsInput(
                WeekLabel: w.WeekLabel,
                Monday: w.Monday,
                TicketCount: w.TicketCount,
                Revenue: w.Revenue,
                StripeFees: w.StripeFees,
                TicketTailorFees: w.TicketTailorFees))
            .ToList();

        var changed = await _repository.SyncTicketingActualsAsync(
            budgetYearId, actualsInputs, today, now, ct);

        _logger.LogInformation(
            "Ticketing budget sync: {Created} line items created/updated for {Weeks} actual weeks + projections",
            changed, weeklyActuals.Count);

        return changed;
    }

    public async Task<int> RefreshTicketingProjectionsAsync(Guid budgetYearId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var today = now.InUtc().Date;

        var created = await _repository.RefreshTicketingProjectionsAsync(budgetYearId, today, now, ct);

        _logger.LogInformation("Ticketing projections refreshed: {Count} line items", created);
        return created;
    }

    public async Task<IReadOnlyList<TicketingWeekProjection>> GetTicketingProjectionEntriesAsync(
        Guid budgetGroupId, CancellationToken ct = default)
    {
        var group = await _repository.GetGroupByIdAsync(budgetGroupId, ct);
        if (group is null || !group.IsTicketingGroup)
            return [];

        var projection = await _repository.GetTicketingProjectionAsync(budgetGroupId, ct);
        if (projection is null)
            return [];

        if (projection.StartDate is null
            || projection.EventDate is null
            || projection.AverageTicketPrice == 0)
        {
            return [];
        }

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
            if (item.Description.StartsWith("Projected: ", StringComparison.Ordinal)) continue;
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
