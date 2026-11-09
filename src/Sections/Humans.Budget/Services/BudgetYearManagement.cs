using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Budget.Data;
using Humans.Budget.Domain;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Budget.Services;

internal sealed class BudgetYearManagement(
    IBudgetRepository repository,
    ITeamService teamService,
    IClock clock,
    ILogger<BudgetService> logger) : IApplicationService
{
    public async Task<BudgetYearDetail> CreateYearAsync(string year, string name, Guid actorUserId)
    {
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

        return new BudgetYearDetail(
            draft.Id,
            year,
            name,
            BudgetYearStatus.Draft,
            IsDeleted: false,
            Groups: []);
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
        var now = clock.GetCurrentInstant();

        var result = await repository.UpdateYearAsync(
            yearId, year, name, string.IsNullOrWhiteSpace(vendorEventId) ? null : vendorEventId.Trim(), actorUserId, now);
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
