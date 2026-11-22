using System.Globalization;
using Humans.Base.Extensions;
using Humans.Budget.Contracts;
using Humans.Budget.Data;
using Humans.Teams.Contracts;
using Humans.Budget.Domain;
using Humans.Gdpr.Contracts;
using Humans.Finance.Contracts;
using NodaTime;
using Humans.Users.Contracts;
using Humans.Base.Interfaces;

namespace Humans.Budget.Services;

/// <summary>budget_audit_logs is append-only.</summary>
internal sealed class BudgetService(
    IBudgetRepository repository,
    ITeamService teamService,
    IUserService userService,
    IClock clock,
    ILogger<BudgetService> logger) : IBudgetManagement, IUserDataContributor
{
    /// <summary>GDPR export JSON key.</summary>
    internal const string BudgetAuditLog = "BudgetAuditLog";

    public async Task<BudgetSaveResult<BudgetGroupDetail>> CreateGroupAsync(
        Guid budgetYearId, string name, bool isRestricted, Guid actorUserId)
    {
        if (TextRefusal(name, "Name", 256) is { } textRefusal)
            return new(Refusal: textRefusal);

        var now = clock.GetCurrentInstant();

        var group = await repository.CreateGroupAsync(
            budgetYearId, name, isRestricted, actorUserId, now);

        logger.LogInformation(
            "Created budget group '{Name}' in year {YearId}", name, budgetYearId);

        return new(ToGroupDetail(group));
    }

    public async Task<BudgetSaveResult<BudgetGroupDetail>> UpdateGroupAsync(
        Guid groupId, string name, int sortOrder, bool isRestricted, Guid actorUserId)
    {
        if (TextRefusal(name, "Name", 256) is { } textRefusal)
            return new(Refusal: textRefusal);

        var now = clock.GetCurrentInstant();

        var ok = await repository.UpdateGroupAsync(
            groupId, name, sortOrder, isRestricted, actorUserId, now);
        if (!ok)
            return new(Refusal: $"Budget group {groupId} not found");
        return new();
    }

    public async Task<BudgetSaveResult<BudgetGroupDetail>> DeleteGroupAsync(Guid groupId, Guid actorUserId)
    {
        var now = clock.GetCurrentInstant();

        var ok = await repository.DeleteGroupAsync(groupId, actorUserId, now);
        if (!ok)
            return new(Refusal: $"Budget group {groupId} not found");

        logger.LogInformation("Deleted budget group {GroupId}", groupId);
        return new();
    }

    public async Task<BudgetSaveResult<BudgetCategoryDetail>> CreateCategoryAsync(
        Guid budgetGroupId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid? teamId, Guid actorUserId)
    {
        if (TextRefusal(name, "Name", 256) is { } textRefusal)
            return new(Refusal: textRefusal);

        if (ValidateMoneyAmount(allocatedAmount) is { } refusal)
            return new(Refusal: refusal);

        var now = clock.GetCurrentInstant();

        var category = await repository.CreateCategoryAsync(
            budgetGroupId, name, allocatedAmount, expenditureType, teamId, actorUserId, now);

        logger.LogInformation(
            "Created budget category '{Name}' in group {GroupId}", name, budgetGroupId);

        return new(ToCategoryDetail(category));
    }

    public async Task<BudgetSaveResult<BudgetCategoryDetail>> UpdateCategoryAsync(
        Guid categoryId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid actorUserId)
    {
        if (TextRefusal(name, "Name", 256) is { } textRefusal)
            return new(Refusal: textRefusal);

        if (ValidateMoneyAmount(allocatedAmount) is { } refusal)
            return new(Refusal: refusal);

        var now = clock.GetCurrentInstant();

        var ok = await repository.UpdateCategoryAsync(
            categoryId, name, allocatedAmount, expenditureType, actorUserId, now);
        if (!ok)
            return new(Refusal: $"Budget category {categoryId} not found");
        return new();
    }

    public async Task<BudgetSaveResult<BudgetCategoryDetail>> DeleteCategoryAsync(Guid categoryId, Guid actorUserId)
    {
        var now = clock.GetCurrentInstant();

        var ok = await repository.DeleteCategoryAsync(categoryId, actorUserId, now);
        if (!ok)
            return new(Refusal: $"Budget category {categoryId} not found");

        logger.LogInformation("Deleted budget category {CategoryId}", categoryId);
        return new();
    }

    public async Task<BudgetSaveResult> CreateLineItemAsync(
        Guid budgetCategoryId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, LocalDate? expectedDate,
        int vatRate, Guid actorUserId)
    {
        if ((TextRefusal(description, "Description", 500)
             ?? TextRefusal(notes, "Notes", 2000, required: false)) is { } textRefusal)
            return new(Refusal: textRefusal);

        if (ValidateMoneyAmount(amount) is { } refusal)
            return new(Refusal: refusal);

        if (VatRateRefusal(vatRate) is { } vatRefusal)
            return new(Refusal: vatRefusal);

        var now = clock.GetCurrentInstant();

        var draft = new BudgetLineItemDraft(
            BudgetCategoryId: budgetCategoryId,
            Description: description,
            Amount: amount,
            ResponsibleTeamId: responsibleTeamId,
            Notes: notes,
            ExpectedDate: expectedDate,
            VatRate: vatRate);

        await repository.CreateLineItemAsync(draft, actorUserId, now);

        logger.LogInformation(
            "Created line item '{Description}' in category {CategoryId}",
            description, budgetCategoryId);

        return new();
    }

    public async Task<BudgetSaveResult> UpdateLineItemAsync(
        Guid lineItemId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, LocalDate? expectedDate,
        int vatRate, Guid actorUserId)
    {
        if ((TextRefusal(description, "Description", 500)
             ?? TextRefusal(notes, "Notes", 2000, required: false)) is { } textRefusal)
            return new(Refusal: textRefusal);

        if (ValidateMoneyAmount(amount) is { } refusal)
            return new(Refusal: refusal);

        if (VatRateRefusal(vatRate) is { } vatRefusal)
            return new(Refusal: vatRefusal);

        var now = clock.GetCurrentInstant();

        var update = new BudgetLineItemUpdate(
            LineItemId: lineItemId,
            Description: description,
            Amount: amount,
            ResponsibleTeamId: responsibleTeamId,
            Notes: notes,
            ExpectedDate: expectedDate,
            VatRate: vatRate);

        var ok = await repository.UpdateLineItemAsync(update, actorUserId, now);
        if (!ok)
            return new(Refusal: $"Budget line item {lineItemId} not found");
        return new();
    }

    private static string? TextRefusal(string? value, string label, int maxLength, bool required = true)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            return $"{label} is required.";
        return value is not null && value.EnumerateRunes().Count() > maxLength
            ? $"{label} must be at most {maxLength} characters."
            : null;
    }

    private static string? ValidateMoneyAmount(decimal amount)
    {
        const decimal maxAmount = 9_999_999_999_999_999.99m;
        return amount < -maxAmount || amount > maxAmount || decimal.Round(amount, 2) != amount
            ? "Amounts must fit within 16 whole digits and two decimal places."
            : null;
    }

    private static string? ValidateFeePercentage(decimal percentage)
    {
        const decimal maxPercentage = 99_999_999_999_999.9999m;
        return percentage < -maxPercentage || percentage > maxPercentage || decimal.Round(percentage, 4) != percentage
            ? "Fee percentages must fit within 14 whole digits and four decimal places."
            : null;
    }

    private static string? VatRateRefusal(int vatRate) =>
        vatRate is < 0 or > 21 ? "VAT rate must be between 0 and 21." : null;

    public async Task<BudgetSaveResult> DeleteLineItemAsync(Guid lineItemId, Guid actorUserId)
    {
        var now = clock.GetCurrentInstant();

        var ok = await repository.DeleteLineItemAsync(lineItemId, actorUserId, now);
        if (!ok)
            return new(Refusal: $"Budget line item {lineItemId} not found");

        logger.LogInformation("Deleted line item {LineItemId}", lineItemId);
        return new();
    }

    public async Task<TicketingProjectionSaveResult> UpdateTicketingProjectionAsync(
        Guid budgetGroupId, LocalDate? startDate, LocalDate? eventDate,
        int initialSalesCount, decimal dailySalesRate, decimal averageTicketPrice, int vatRate,
        decimal stripeFeePercent, decimal stripeFeeFixed, decimal ticketTailorFeePercent, Guid actorUserId,
        bool refreshProjections = false, CancellationToken ct = default)
    {
        if (VatRateRefusal(vatRate) is { } vatRefusal)
            return new(Refusal: vatRefusal);

        if ((ValidateMoneyAmount(dailySalesRate)
             ?? ValidateMoneyAmount(averageTicketPrice)
             ?? ValidateMoneyAmount(stripeFeeFixed)
             ?? ValidateFeePercentage(stripeFeePercent)
             ?? ValidateFeePercentage(ticketTailorFeePercent)) is { } decimalRefusal)
            return new(Refusal: decimalRefusal);

        var now = clock.GetCurrentInstant();

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

        var created = await repository.UpdateTicketingProjectionAsync(
            update, actorUserId, now, ct, refreshProjections ? now.InUtc().Date : null);

        logger.LogInformation("Updated ticketing projection for group {GroupId}", budgetGroupId);
        return new(created);
    }

    public BudgetSummaryResult ComputeBudgetSummary(IReadOnlyList<BudgetGroupDetail> groups) =>
        BudgetSummaryCalculator.ComputeBudgetSummary(groups);

    public BudgetSummaryResult ComputeBudgetSummaryWithBuffers(IReadOnlyList<BudgetGroupDetail> groups) =>
        BudgetSummaryCalculator.ComputeBudgetSummaryWithBuffers(groups);

    // The repository materializes projected weeks, so they never lag one sync behind.
    public async Task<int> SyncTicketingActualsAsync(
        Guid budgetYearId,
        IReadOnlyList<TicketingWeeklyActualsInput> weeklyActuals,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var today = now.InUtc().Date;

        var changed = await repository.SyncTicketingActualsAsync(
            budgetYearId, weeklyActuals, today, actorUserId, now, ct);

        logger.LogInformation(
            "Ticketing budget sync: {Created} line items created/updated for {Weeks} actual weeks + projections",
            changed, weeklyActuals.Count);

        return changed;
    }

    public int GetActualTicketsSold(BudgetGroupDetail ticketingGroup)
    {
        var revenueCategory = ticketingGroup.Categories
            .FirstOrDefault(c => string.Equals(c.Name, "Ticket Revenue", StringComparison.Ordinal));

        if (revenueCategory is null) return 0;

        return SumActualTicketsSold(revenueCategory.LineItems
            .Select(item => (item.IsAutoGenerated, item.Description, item.Notes)));
    }

    // From auto-generated revenue notes "N tickets" (projections use "~N").
    private static int SumActualTicketsSold(IEnumerable<(bool IsAutoGenerated, string Description, string? Notes)> items)
    {
        var total = 0;
        foreach (var item in items)
        {
            if (!item.IsAutoGenerated) continue;
            if (item.Description.StartsWith("Projected: ", StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(item.Notes)) continue;

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

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        // Include audit rows left on accounts merged into this one. Asked with an archived id, the survivor's rows
        // count too.
        var allIds = (await userService.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];
        var entries = await repository.GetAuditLogEntriesForUserIdsAsync(allIds, ct);

        var shaped = entries.Select(bal => new
        {
            bal.EntityType,
            bal.FieldName,
            bal.Description,
            OccurredAt = bal.OccurredAt.ToIso8601()
        }).ToList();

        return [new UserDataSlice(BudgetAuditLog, shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [BudgetAuditLog] =
                "Retained: append-only accounting audit ledger. Spanish law requires the books " +
                "and their supporting records be kept 6 years (Código de Comercio Art. 30) and " +
                "4 years for tax purposes (Ley 58/2003 Art. 66) — GDPR Art. 17(3)(b)."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;

    public async Task<IReadOnlyList<BudgetYearSummarySnapshot>> GetAllYearsAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var years = await repository.GetAllYearsAsync(includeArchived, ct);
        return years.Select(ToYearSummarySnapshot).ToList();
    }

    private static BudgetYearSummarySnapshot ToYearSummarySnapshot(BudgetYear year) =>
        new(
            year.Id,
            year.Year,
            year.Name,
            year.Status,
            year.IsDeleted,
            year.Groups
                .Select(group => new BudgetGroupSummarySnapshot(
                    group.Id,
                    group.Name,
                    group.SortOrder,
                    group.IsRestricted,
                    group.IsDepartmentGroup))
                .ToList(),
            year.VendorEventId);

    public async Task<BudgetYearDetail?> GetYearByIdAsync(Guid id, CancellationToken ct = default)
    {
        var year = await repository.GetYearByIdAsync(id, ct);
        return year is null ? null : ToYearDetail(year);
    }

    public async Task<BudgetYearDetail?> GetActiveYearAsync(CancellationToken ct = default)
    {
        var year = await repository.GetActiveYearAsync(ct);
        return year is null ? null : ToYearDetail(year);
    }

    internal static BudgetYearDetail ToYearDetail(BudgetYear year) =>
        new(
            year.Id,
            year.Year,
            year.Name,
            year.Status,
            year.IsDeleted,
            year.Groups
                .Select(ToGroupDetail)
                .ToList(),
            year.VendorEventId);

    internal static BudgetGroupDetail ToGroupDetail(BudgetGroup group) =>
        new(
            group.Id,
            group.BudgetYearId,
            group.Name,
            group.SortOrder,
            group.IsRestricted,
            group.IsDepartmentGroup,
            group.IsTicketingGroup,
            group.TicketingProjection is null
                ? null
                : new TicketingProjectionSnapshot(
                    group.TicketingProjection.Id,
                    group.TicketingProjection.BudgetGroupId,
                    group.TicketingProjection.StartDate,
                    group.TicketingProjection.EventDate,
                    group.TicketingProjection.InitialSalesCount,
                    group.TicketingProjection.DailySalesRate,
                    group.TicketingProjection.AverageTicketPrice,
                    group.TicketingProjection.VatRate,
                    group.TicketingProjection.StripeFeePercent,
                    group.TicketingProjection.StripeFeeFixed,
                    group.TicketingProjection.TicketTailorFeePercent,
                    group.TicketingProjection.CreatedAt,
                    group.TicketingProjection.UpdatedAt),
            group.Categories
                .Select(ToCategoryDetail)
                .ToList());

    internal static BudgetCategoryDetail ToCategoryDetail(BudgetCategory category) =>
        new(
            category.Id,
            category.BudgetGroupId,
            category.Name,
            category.AllocatedAmount,
            category.ExpenditureType,
            category.TeamId,
            category.SortOrder,
            category.LineItems
                .Select(item => new BudgetLineItemDetail(
                    item.Id,
                    item.BudgetCategoryId,
                    item.Description,
                    item.Amount,
                    item.ResponsibleTeamId,
                    item.Notes,
                    item.ExpectedDate,
                    item.VatRate,
                    item.IsAutoGenerated,
                    item.IsCashflowOnly,
                    item.SortOrder))
                .ToList());

    public async Task<BudgetCategorySnapshot?> GetCategoryByIdAsync(Guid id, CancellationToken ct = default)
    {
        var category = await repository.GetCategoryByIdAsync(id, ct);
        if (category is null)
            return null;

        var teamIds = category.LineItems
            .Select(li => li.ResponsibleTeamId)
            .Where(tid => tid.HasValue)
            .Select(tid => tid!.Value)
            .Distinct()
            .ToList();

        var teamNamesById = new Dictionary<Guid, string>();
        if (teamIds.Count > 0)
        {
            var teams = await teamService.GetTeamsAsync(ct);
            foreach (var teamId in teamIds)
            {
                if (teams.TryGetValue(teamId, out var team))
                    teamNamesById[teamId] = team.Name;
            }
        }

        return ToCategorySnapshot(category, teamNamesById);
    }

    public async Task<IReadOnlyList<TeamInfo>> GetActiveTeamsAsync(CancellationToken ct = default) =>
        (await teamService.GetTeamsAsync(ct)).Values.Where(team => team.IsActive).ToList();

    private static BudgetCategorySnapshot ToCategorySnapshot(
        BudgetCategory category,
        IReadOnlyDictionary<Guid, string> teamNamesById) =>
        new(
            category.Id,
            category.BudgetGroupId,
            category.Name,
            category.AllocatedAmount,
            category.ExpenditureType,
            category.TeamId,
            category.SortOrder,
            category.BudgetGroup is null
                ? null
                : new BudgetCategoryGroupSnapshot(
                    category.BudgetGroup.Id,
                    category.BudgetGroup.BudgetYearId,
                    category.BudgetGroup.Name,
                    category.BudgetGroup.IsRestricted,
                    category.BudgetGroup.IsTicketingGroup,
                    category.BudgetGroup.BudgetYear is null
                        ? null
                        : new BudgetCategoryYearSnapshot(
                            category.BudgetGroup.BudgetYear.Id,
                            category.BudgetGroup.BudgetYear.Year,
                            category.BudgetGroup.BudgetYear.Name,
                            category.BudgetGroup.BudgetYear.IsDeleted)),
            category.LineItems
                .Select(item => new BudgetCategoryLineItemSnapshot(
                    item.Id,
                    item.BudgetCategoryId,
                    item.Description,
                    item.Amount,
                    item.ResponsibleTeamId,
                    item.ResponsibleTeamId is { } rtid && teamNamesById.TryGetValue(rtid, out var name) ? name : null,
                    item.Notes,
                    item.ExpectedDate,
                    item.VatRate,
                    item.IsAutoGenerated,
                    item.IsCashflowOnly,
                    item.SortOrder))
                .ToList());

    public Task<Guid?> GetLineItemCategoryIdAsync(Guid id, CancellationToken ct = default) =>
        repository.GetLineItemCategoryIdAsync(id, ct);

    public async Task<IReadOnlyList<BudgetAuditLogSnapshot>> GetAuditLogAsync(Guid? budgetYearId, CancellationToken ct = default)
    {
        var entries = await repository.GetAuditLogAsync(budgetYearId, ct);
        return entries.Select(e => new BudgetAuditLogSnapshot(
            e.Id,
            e.BudgetYearId,
            e.EntityType,
            e.EntityId,
            e.FieldName,
            e.OldValue,
            e.NewValue,
            e.Description,
            e.ActorUserId,
            e.OccurredAt)).ToList();
    }

    public async Task<int> GetProjectedRemainingTicketsAsync(
        Guid budgetGroupId, CancellationToken ct = default)
    {
        var projection = await repository.GetTicketingProjectionAsync(budgetGroupId, ct);
        return projection?.CalculateWeeks(clock.GetCurrentInstant().InUtc().Date)
            .Sum(week => week.Tickets) ?? 0;
    }

    public async Task<BudgetSaveResult<BudgetYearDetail>> CreateYearAsync(string year, string name, Guid actorUserId)
    {
        if ((TextRefusal(year, "Year identifier", 50)
             ?? TextRefusal(name, "Name", 256)) is { } textRefusal)
            return new(Refusal: textRefusal);

        var now = clock.GetCurrentInstant();

        var teamRefs = await GetBudgetableTeamsAsync();

        var draft = new BudgetYearDraft(
            Id: Guid.NewGuid(),
            Year: year,
            Name: name,
            BudgetableTeams: teamRefs,
            ActorUserId: actorUserId,
            Now: now);

        await repository.CreateYearWithScaffoldAsync(draft);

        logger.LogInformation(
            "Created budget year {Year} ({Name}) with {TeamCount} department categories",
            year, name, teamRefs.Count);

        return new(new BudgetYearDetail(
            draft.Id,
            year,
            name,
            BudgetYearStatus.Draft,
            IsDeleted: false,
            Groups: []));
    }

    public async Task<BudgetYearMutationResult> UpdateYearStatusAsync(Guid yearId, BudgetYearStatus status, Guid actorUserId)
    {
        var now = clock.GetCurrentInstant();

        var result = await repository.UpdateYearStatusAsync(yearId, status, actorUserId, now);
        if (!result.Succeeded) return result;

        logger.LogInformation(
            "Updated budget year {YearId} status to {NewStatus}",
            yearId, status);
        return result;
    }

    public async Task<BudgetYearMutationResult> UpdateYearAsync(Guid yearId, string year, string name, string? vendorEventId, Guid actorUserId)
    {
        vendorEventId = string.IsNullOrWhiteSpace(vendorEventId) ? null : vendorEventId.Trim();

        if ((TextRefusal(year, "Year identifier", 50)
             ?? TextRefusal(name, "Name", 256)
             ?? TextRefusal(vendorEventId, "Vendor event ID", 256, required: false)) is { } textRefusal)
            return new(Refusal: textRefusal);

        var now = clock.GetCurrentInstant();

        var result = await repository.UpdateYearAsync(
            yearId, year, name, vendorEventId, actorUserId, now);
        return result;
    }

    public async Task<BudgetYearMutationResult> DeleteYearAsync(Guid yearId, Guid actorUserId)
    {
        var now = clock.GetCurrentInstant();

        var result = await repository.SoftDeleteYearAsync(yearId, actorUserId, now);
        if (!result.Succeeded) return result;

        logger.LogInformation("Archived budget year {YearId}", yearId);
        return result;
    }

    private async Task<List<BudgetableTeamRef>> GetBudgetableTeamsAsync() =>
        (await teamService.GetTeamsAsync()).Values
            .Where(t => t.IsActive && t.HasBudget)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new BudgetableTeamRef(t.Id, t.Name))
            .ToList();

    public async Task<int> SyncDepartmentsAsync(Guid budgetYearId, Guid actorUserId)
    {
        var now = clock.GetCurrentInstant();

        var teamRefs = await GetBudgetableTeamsAsync();

        var created = await repository.SyncDepartmentCategoriesAsync(
            budgetYearId, teamRefs, actorUserId, now);

        if (created > 0)
            logger.LogInformation(
                "Synced {Count} departments into budget year {YearId}",
                created, budgetYearId);

        return created;
    }

    public async Task<EnsureTicketingGroupResult> EnsureTicketingGroupAsync(Guid budgetYearId, Guid actorUserId)
    {
        var now = clock.GetCurrentInstant();

        var created = await repository.EnsureTicketingGroupAsync(budgetYearId, actorUserId, now);

        if (created)
            logger.LogInformation("Added ticketing group to budget year {YearId}", budgetYearId);

        return new EnsureTicketingGroupResult(
            created,
            created
                ? "Ticketing group added to this budget year."
                : "Ticketing group already exists for this budget year.");
    }
}
