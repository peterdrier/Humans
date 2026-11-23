using Humans.Base.Demo;
using Humans.Teams.Contracts;

namespace Humans.Teams.Services.Demo;

/// <summary>
/// The demo departments of <see cref="TeamsDemo.Teams"/>, each staffed by active cast volunteers, two of them its
/// coordinators. Membership is checked on every run, so a member a tester removed comes back.
/// </summary>
internal sealed class TeamsDemoStory(ITeamManagementService teams, ITeamService teamReads) : IDemoStory
{
    private const string NameSuffix = " (demo)";
    private const string CoordinatorRole = "Coordinator";
    private const int CoordinatorCount = 2;

    public DemoStoryKey Key => TeamsDemo.StoryKey;

    public IReadOnlyList<DemoStoryKey> After => [DemoCast.StoryKey];

    public async Task TellAsync(IDemoWorld world, CancellationToken ct)
    {
        var crowd = await world.MadeAsync(DemoCast.HumanKeyPrefix, ct);
        var volunteers = Enumerable.Range(1, DemoCast.Size)
            .Where(DemoCast.IsActiveVolunteer)
            .Select(n => crowd.GetValueOrDefault(DemoCast.HumanKey(n)))
            .Where(id => id != Guid.Empty)
            .ToList();

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var i = 0; i < TeamsDemo.Teams.Count; i++)
        {
            var team = TeamsDemo.Teams[i];
            Guid? parentId = team.ParentKey is null ? null : ids[team.ParentKey];
            ids[team.Key] = await world.EnsureAsync(
                team.Key,
                async (id, token) => await teamReads.GetTeamAsync(id, token) is { IsActive: true },
                (_, token) => CreateAsync(team, parentId, token),
                ct);

            // Every team draws a different slice of the crowd; parents get more people than subteams.
            var size = team.ParentKey is null ? 10 : 4;
            var members = volunteers.Where((_, index) => index % TeamsDemo.Teams.Count == i).Take(size).ToList();
            await StaffAsync(ids[team.Key], members, world.ActorUserId, ct);
        }
    }

    public async Task RetireAsync(IDemoWorld world, CancellationToken ct)
    {
        var made = await world.MadeAsync(TeamsDemo.TeamKeyPrefix, ct);

        // Subteams first: a parent with active children stays put.
        foreach (var team in TeamsDemo.Teams.Reverse())
        {
            if (!made.TryGetValue(team.Key, out var teamId))
                continue;

            var result = await teams.DeleteTeamAsync(teamId, world.ActorUserId, ct);
            if (result.Succeeded)
                await world.MarkRetiredAsync(team.Key, ct);
            else
                await world.MarkLeftBehindAsync(team.Key, $"deactivation refused: {result.Refusal}", ct);
        }
    }

    // Teams mints its own ids, so a run cut off after the create finds its team by slug. Staffing comes after the
    // ledger records the team, so the demo's half-made team has no members; one with members is someone else's.
    private async Task<Guid> CreateAsync(DemoTeam team, Guid? parentId, CancellationToken ct)
    {
        var name = team.Name + NameSuffix;
        var existing = await teamReads.GetTeamBySlugAsync(Base.Helpers.SlugHelper.GenerateSlug(name), ct);
        if (existing is { IsActive: true })
        {
            return existing.Members.Count == 0
                ? existing.Id
                : throw new InvalidOperationException($"A team named '{name}' already exists and the demo did not make it. Rename it, then top up.");
        }

        var created = await teams.CreateTeamAsync(name, description: null, requiresApproval: true, parentId, cancellationToken: ct);
        return created.Id;
    }

    private async Task StaffAsync(Guid teamId, IReadOnlyList<Guid> members, Guid actorUserId, CancellationToken ct)
    {
        var team = await teamReads.GetTeamAsync(teamId, ct)
            ?? throw new InvalidOperationException($"Demo team {teamId} vanished while being staffed.");
        var current = team.Members.Select(m => m.UserId).ToHashSet();

        foreach (var userId in members.Where(id => !current.Contains(id)))
        {
            var added = await teams.AddMemberToTeamAsync(teamId, userId, actorUserId, ct);
            if (!added.Succeeded)
                throw new InvalidOperationException($"Could not add a demo member to '{team.Name}': {added.Refusal ?? added.ErrorKey}");
        }

        var definitions = await teams.GetRoleDefinitionsAsync(teamId, ct);
        var coordinator = definitions.FirstOrDefault(d => d.IsManagement);
        if (coordinator is null)
        {
            var created = await teams.CreateRoleDefinitionAsync(
                teamId, CoordinatorRole, description: null, slotCount: CoordinatorCount, priorities: [SlotPriority.Critical, SlotPriority.Important],
                sortOrder: 0, RolePeriod.YearRound, actorUserId, cancellationToken: ct);
            var definitionId = created.Definition?.Id
                ?? throw new InvalidOperationException($"Could not add a coordinator role to '{team.Name}': {created.Refusal}");
            var toggled = await teams.ToggleRoleIsManagementAsync(definitionId, actorUserId, ct);
            if (!toggled.Succeeded)
                throw new InvalidOperationException($"Could not make '{team.Name}' coordinators managers: {toggled.Refusal}");
            coordinator = (await teams.GetRoleDefinitionsAsync(teamId, ct)).Single(d => d.Id == definitionId);
        }

        // A coordinator a tester put in counts: the demo only fills empty seats.
        var holders = team.ManagementRoleHolderUserIds ?? new HashSet<Guid>();
        var missing = CoordinatorCount - holders.Count;
        foreach (var userId in members.Where(id => !holders.Contains(id)).Take(Math.Max(0, missing)))
        {
            var assigned = await teams.AssignToRoleAsync(coordinator.Id, userId, actorUserId, ct);
            if (!assigned.Succeeded)
                throw new InvalidOperationException($"Could not make a demo coordinator in '{team.Name}': {assigned.Refusal}");
        }
    }
}
