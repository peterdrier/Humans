using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Infrastructure;

public sealed record DevelopmentBudgetSeedResult(
    Guid BudgetYearId,
    string BudgetYearCode,
    string BudgetYearName,
    bool ActivatedBudgetYear,
    int TeamsCreated,
    int TeamsUpdated,
    int DepartmentCategoriesSynced,
    int GroupsCreated,
    int CategoriesCreated,
    int LineItemsCreated);

public sealed class DevelopmentBudgetSeeder
{
    private static readonly BudgetTeamSeed[] TeamSeeds =
    [
        new(
            Slug: "demo-kitchen",
            Name: "Demo Kitchen",
            Description: "Food, water, and volunteer hydration operations.",
            AllocatedAmount: -55199.22m,
            ExpenditureType: ExpenditureType.OpEx,
            LineItems:
            [
                new("Dry goods and staples", -21230.47m, "Bulk pantry order for crew meals and volunteer kitchen stock.", new LocalDate(2026, 4, 16), 10),
                new("Cold storage rental", -17483.92m, "Walk-in refrigeration for perishables across build and event week.", new LocalDate(2026, 5, 8), 21),
                new("Fresh produce top-up", -11551.87m, "Mid-season restock for the final kitchen push.", new LocalDate(2026, 6, 19), 10),
                new("Water station consumables", -4932.96m, "Cups, filtration cartridges, and cleaning supplies.", new LocalDate(2026, 7, 6), 21)
            ]),
        new(
            Slug: "demo-site-ops",
            Name: "Demo Site Ops",
            Description: "Core site readiness, lighting, fencing, and directional systems.",
            AllocatedAmount: -82111.96m,
            ExpenditureType: ExpenditureType.OpEx,
            LineItems:
            [
                new("Perimeter fencing rental", -32470.13m, "Event perimeter, backstage zoning, and access control fencing.", new LocalDate(2026, 4, 24), 21),
                new("Portable lighting towers", -26850.30m, "Night-safe lighting coverage for work zones and public paths.", new LocalDate(2026, 5, 15), 21),
                new("Wayfinding and safety signage", -14986.21m, "Directional signs, hazard boards, and reflective markers.", new LocalDate(2026, 6, 3), 21),
                new("Tooling and repairs", -7805.32m, "Consumables and small repairs during build week.", new LocalDate(2026, 7, 2), 21)
            ]),
        new(
            Slug: "demo-welfare",
            Name: "Demo Welfare",
            Description: "Shade, wellbeing, first aid, and volunteer care.",
            AllocatedAmount: -37215.76m,
            ExpenditureType: ExpenditureType.OpEx,
            LineItems:
            [
                new("Shade structures and soft seating", -16547.28m, "Quiet-zone shade, beanbags, and recovery seating.", new LocalDate(2026, 4, 29), 21),
                new("First aid replenishment", -8866.84m, "Restock of trauma packs, consumables, and sunscreen.", new LocalDate(2026, 5, 28), 21),
                new("Hydration support", -6119.37m, "Electrolytes, coolers, and water transport backup.", new LocalDate(2026, 6, 17), 10),
                new("Volunteer wellbeing budget", -5682.27m, "Hot-weather extras and crew decompression supplies.", new LocalDate(2026, 7, 9), 21)
            ]),
        new(
            Slug: "demo-build-crew",
            Name: "Demo Build Crew",
            Description: "Build and strike materials, fixings, and shared fabrication support.",
            AllocatedAmount: -64471.94m,
            ExpenditureType: ExpenditureType.CapEx,
            LineItems:
            [
                new("Timber and structural hardware", -29972.43m, "Primary materials for shade, shelving, and public-facing structures.", new LocalDate(2026, 4, 11), 21),
                new("Power distro cabling", -16547.28m, "Cables, connectors, and protective runs for key install areas.", new LocalDate(2026, 5, 20), 21),
                new("Workshop consumables", -10303.02m, "Blades, fixings, abrasives, and adhesives.", new LocalDate(2026, 6, 14), 21),
                new("Strike and recovery transport", -7649.21m, "Shared van hire and late-stage teardown extras.", new LocalDate(2026, 7, 13), 21)
            ])
    ];

    private static readonly BudgetCategorySeed[] SharedServicesCategories =
    [
        new(
            Name: "Insurance & Permits",
            AllocatedAmount: -36966.01m,
            ExpenditureType: ExpenditureType.OpEx,
            LineItems:
            [
                new("Event insurance", -19357.21m, "Public liability and equipment cover for the season.", new LocalDate(2026, 3, 21), 0),
                new("Local permits and filings", -10240.58m, "Permit processing, translations, and administrative fees.", new LocalDate(2026, 4, 5), 0),
                new("Site compliance documentation", -7368.22m, "Occupancy plan, signage review, and external checks.", new LocalDate(2026, 5, 6), 21)
            ]),
        new(
            Name: "Sanctuary & Welfare Reserve",
            AllocatedAmount: -25008.24m,
            ExpenditureType: ExpenditureType.OpEx,
            LineItems:
            [
                new("Accessibility reserve", -11239.66m, "Last-minute accessibility rentals and support.", new LocalDate(2026, 5, 12), 21),
                new("Quiet space fit-out", -8898.06m, "Soft furnishings, blackout materials, and care supplies.", new LocalDate(2026, 6, 8), 21),
                new("De-escalation training", -4870.52m, "Short-format crew support and safeguarding refreshers.", new LocalDate(2026, 6, 21), 0)
            ]),
        new(
            Name: "Infrastructure Upgrades",
            AllocatedAmount: -64190.95m,
            ExpenditureType: ExpenditureType.CapEx,
            LineItems:
            [
                new("Battery bank expansion", -28224.04m, "Additional storage for overnight critical loads.", new LocalDate(2026, 3, 29), 21),
                new("Storage and workshop racks", -14236.90m, "Safer storage layout for tools and spares.", new LocalDate(2026, 4, 18), 21),
                new("Weatherproof staging fixes", -21730.01m, "Reinforcement and replacement of high-wear materials.", new LocalDate(2026, 6, 2), 21)
            ])
    ];

    private static readonly BudgetCategorySeed[] TicketingCategories =
    [
        new(
            Name: "Ticket Revenue",
            AllocatedAmount: 441882.24m,
            ExpenditureType: ExpenditureType.OpEx,
            LineItems:
            [
                new("Early release wave", 115147.45m, "Initial launch burst including supporter allocation.", new LocalDate(2026, 1, 18), 10),
                new("Main sale weekend", 152994.85m, "Primary public sales window after line-up announcement.", new LocalDate(2026, 3, 7), 10),
                new("Final release", 173739.94m, "Late-phase sales including partner holds conversion.", new LocalDate(2026, 5, 29), 10)
            ]),
        new(
            Name: "Processing Fees",
            AllocatedAmount: -24664.81m,
            ExpenditureType: ExpenditureType.OpEx,
            LineItems:
            [
                new("Stripe variable fees", -9303.94m, "Percentage fees across card payments.", new LocalDate(2026, 1, 18), 21),
                new("TicketTailor application fees", -13393.93m, "Vendor fees net of waived organizer promos.", new LocalDate(2026, 3, 7), 21),
                new("Refund handling and chargebacks", -1966.94m, "Refund costs and disputed payment handling.", new LocalDate(2026, 6, 11), 0)
            ])
    ];

    private readonly IBudgetService _budgetService;
    private readonly ITeamService _teamService;
    private readonly ICampService _campService;
    private readonly IClock _clock;
    private readonly ILogger<DevelopmentBudgetSeeder> _logger;

    public DevelopmentBudgetSeeder(
        IBudgetService budgetService,
        ITeamService teamService,
        ICampService campService,
        IClock clock,
        ILogger<DevelopmentBudgetSeeder> logger)
    {
        _budgetService = budgetService;
        _teamService = teamService;
        _campService = campService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DevelopmentBudgetSeedResult> SeedAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var campSettings = await _campService.GetSettingsAsync(cancellationToken);
        var publicYear = campSettings.PublicYear;

        if (publicYear <= 0)
        {
            publicYear = now.InUtc().Year;
        }

        var budgetYearCode = $"DEMO-{publicYear}";
        var budgetYearName = $"Demo Budget {publicYear}";

        var teamsCreated = 0;
        var teamsUpdated = 0;
        foreach (var seed in TeamSeeds)
        {
            await EnsureBudgetTeamAsync(seed, cancellationToken, onCreated: () => teamsCreated++, onUpdated: () => teamsUpdated++);
        }

        var allYears = await _budgetService.GetAllYearsAsync(includeArchived: true);
        var budgetYear = allYears.FirstOrDefault(y => string.Equals(y.Year, budgetYearCode, StringComparison.Ordinal));

        if (budgetYear is null)
        {
            budgetYear = await _budgetService.CreateYearAsync(budgetYearCode, budgetYearName, actorUserId);
        }
        else if (budgetYear.IsDeleted)
        {
            await _budgetService.RestoreYearAsync(budgetYear.Id, actorUserId);
        }

        var departmentCategoriesSynced = await _budgetService.SyncDepartmentsAsync(budgetYear.Id, actorUserId);

        var groupsCreated = 0;
        if (await _budgetService.EnsureTicketingGroupAsync(budgetYear.Id, actorUserId))
        {
            groupsCreated++;
        }

        var activeYear = await _budgetService.GetActiveYearAsync();
        var activatedBudgetYear = false;
        if (activeYear is null)
        {
            await _budgetService.UpdateYearStatusAsync(budgetYear.Id, BudgetYearStatus.Active, actorUserId);
            activatedBudgetYear = true;
        }

        // Load full year tree — groups, categories, line items — for in-memory lookups
        var currentYear = await _budgetService.GetYearByIdAsync(budgetYear.Id)
            ?? throw new InvalidOperationException($"Budget year {budgetYear.Id} not found after creation");

        var departmentGroup = currentYear.Groups.Single(g => g.IsDepartmentGroup);
        await _budgetService.UpdateGroupAsync(departmentGroup.Id, departmentGroup.Name, 0, departmentGroup.IsRestricted, actorUserId);

        var sharedServicesGroup = currentYear.Groups.FirstOrDefault(g =>
            string.Equals(g.Name, "Shared Services", StringComparison.Ordinal));

        if (sharedServicesGroup is null)
        {
            sharedServicesGroup = await _budgetService.CreateGroupAsync(budgetYear.Id, "Shared Services", false, actorUserId);
            groupsCreated++;
        }

        await _budgetService.UpdateGroupAsync(sharedServicesGroup.Id, sharedServicesGroup.Name, 1, sharedServicesGroup.IsRestricted, actorUserId);

        var ticketingGroup = currentYear.Groups.Single(g => g.IsTicketingGroup);
        await _budgetService.UpdateGroupAsync(ticketingGroup.Id, ticketingGroup.Name, 2, ticketingGroup.IsRestricted, actorUserId);

        var categoriesCreated = 0;
        var lineItemsCreated = 0;

        // Re-load after group changes for accurate category lookups
        currentYear = await _budgetService.GetYearByIdAsync(budgetYear.Id)
            ?? throw new InvalidOperationException($"Budget year {budgetYear.Id} not found");

        departmentGroup = currentYear.Groups.Single(g => g.IsDepartmentGroup);
        sharedServicesGroup = currentYear.Groups.Single(g =>
            string.Equals(g.Name, "Shared Services", StringComparison.Ordinal));
        ticketingGroup = currentYear.Groups.Single(g => g.IsTicketingGroup);

        foreach (var teamSeed in TeamSeeds)
        {
            var team = await _teamService.GetTeamBySlugAsync(teamSeed.Slug, cancellationToken)
                ?? throw new InvalidOperationException($"Team with slug '{teamSeed.Slug}' not found after seeding");

            var category = departmentGroup.Categories.FirstOrDefault(c => c.TeamId == team.Id);
            if (category is null)
            {
                category = await _budgetService.CreateCategoryAsync(
                    departmentGroup.Id, teamSeed.Name, teamSeed.AllocatedAmount, teamSeed.ExpenditureType, team.Id, actorUserId);
                categoriesCreated++;
            }

            await SeedLineItemsAsync(category, teamSeed.LineItems, actorUserId, cancellationToken, () => lineItemsCreated++);
        }

        foreach (var sharedSeed in SharedServicesCategories)
        {
            var category = sharedServicesGroup.Categories.FirstOrDefault(c =>
                string.Equals(c.Name, sharedSeed.Name, StringComparison.Ordinal));

            if (category is null)
            {
                category = await _budgetService.CreateCategoryAsync(
                    sharedServicesGroup.Id, sharedSeed.Name, sharedSeed.AllocatedAmount, sharedSeed.ExpenditureType, null, actorUserId);
                categoriesCreated++;
            }

            await SeedLineItemsAsync(category, sharedSeed.LineItems, actorUserId, cancellationToken, () => lineItemsCreated++);
        }

        foreach (var ticketSeed in TicketingCategories)
        {
            var category = ticketingGroup.Categories.FirstOrDefault(c =>
                string.Equals(c.Name, ticketSeed.Name, StringComparison.Ordinal));

            if (category is null)
            {
                category = await _budgetService.CreateCategoryAsync(
                    ticketingGroup.Id, ticketSeed.Name, ticketSeed.AllocatedAmount, ticketSeed.ExpenditureType, null, actorUserId);
                categoriesCreated++;
            }

            await SeedLineItemsAsync(category, ticketSeed.LineItems, actorUserId, cancellationToken, () => lineItemsCreated++);
        }

        await _budgetService.UpdateTicketingProjectionAsync(
            ticketingGroup.Id,
            startDate: new LocalDate(publicYear, 1, 15),
            eventDate: new LocalDate(publicYear, 8, 24),
            initialSalesCount: 550,
            dailySalesRate: 8.5m,
            averageTicketPrice: 250m,
            vatRate: 10,
            stripeFeePercent: 1.50m,
            stripeFeeFixed: 0.25m,
            ticketTailorFeePercent: 3.00m,
            actorUserId);

        _logger.LogInformation(
            "Development budget seed completed for {BudgetYearCode}: teamsCreated={TeamsCreated}, teamsUpdated={TeamsUpdated}, categoriesCreated={CategoriesCreated}, lineItemsCreated={LineItemsCreated}",
            budgetYearCode, teamsCreated, teamsUpdated, categoriesCreated, lineItemsCreated);

        return new DevelopmentBudgetSeedResult(
            BudgetYearId: budgetYear.Id,
            BudgetYearCode: budgetYearCode,
            BudgetYearName: budgetYearName,
            ActivatedBudgetYear: activatedBudgetYear,
            TeamsCreated: teamsCreated,
            TeamsUpdated: teamsUpdated,
            DepartmentCategoriesSynced: departmentCategoriesSynced,
            GroupsCreated: groupsCreated,
            CategoriesCreated: categoriesCreated,
            LineItemsCreated: lineItemsCreated);
    }

    private async Task EnsureBudgetTeamAsync(
        BudgetTeamSeed seed,
        CancellationToken cancellationToken,
        Action onCreated,
        Action onUpdated)
    {
        var existing = await _teamService.GetTeamBySlugAsync(seed.Slug, cancellationToken);

        if (existing is null)
        {
            var team = await _teamService.CreateTeamAsync(
                seed.Name, seed.Description, requiresApproval: true, cancellationToken: cancellationToken);

            await _teamService.UpdateTeamAsync(
                team.Id, team.Name, team.Description, team.RequiresApproval, isActive: true,
                hasBudget: true, isHidden: false, isSensitive: false, cancellationToken: cancellationToken);

            onCreated();
            return;
        }

        await _teamService.UpdateTeamAsync(
            existing.Id, seed.Name, seed.Description, existing.RequiresApproval, isActive: true,
            hasBudget: true, isHidden: false, isSensitive: false, cancellationToken: cancellationToken);

        onUpdated();
    }

    private async Task SeedLineItemsAsync(
        BudgetCategory category,
        IReadOnlyList<BudgetLineItemSeed> lineItems,
        Guid actorUserId,
        CancellationToken cancellationToken,
        Action onLineItemCreated)
    {
        await _budgetService.UpdateCategoryAsync(category.Id, category.Name,
            lineItems.Sum(li => li.Amount), category.ExpenditureType, actorUserId);

        foreach (var lineItem in lineItems)
        {
            var existing = category.LineItems.FirstOrDefault(li =>
                string.Equals(li.Description, lineItem.Description, StringComparison.Ordinal));

            if (existing is null)
            {
                await _budgetService.CreateLineItemAsync(
                    category.Id,
                    lineItem.Description,
                    lineItem.Amount,
                    responsibleTeamId: category.TeamId,
                    notes: lineItem.Notes,
                    expectedDate: lineItem.ExpectedDate,
                    vatRate: lineItem.VatRate,
                    actorUserId);

                onLineItemCreated();
                continue;
            }

            await _budgetService.UpdateLineItemAsync(
                existing.Id,
                lineItem.Description,
                lineItem.Amount,
                responsibleTeamId: category.TeamId,
                notes: lineItem.Notes,
                expectedDate: lineItem.ExpectedDate,
                vatRate: lineItem.VatRate,
                actorUserId);
        }
    }

    private sealed record BudgetTeamSeed(
        string Slug,
        string Name,
        string Description,
        decimal AllocatedAmount,
        ExpenditureType ExpenditureType,
        IReadOnlyList<BudgetLineItemSeed> LineItems);

    private sealed record BudgetCategorySeed(
        string Name,
        decimal AllocatedAmount,
        ExpenditureType ExpenditureType,
        IReadOnlyList<BudgetLineItemSeed> LineItems);

    private sealed record BudgetLineItemSeed(
        string Description,
        decimal Amount,
        string Notes,
        LocalDate ExpectedDate,
        int VatRate);
}
