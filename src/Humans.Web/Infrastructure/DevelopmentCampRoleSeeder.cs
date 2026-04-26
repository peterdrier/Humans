using Humans.Application.Interfaces.Camps;

namespace Humans.Web.Infrastructure;

public sealed record DevelopmentCampRoleSeedResult(int RolesCreated, int RolesAlreadyExisted);

public sealed class DevelopmentCampRoleSeeder
{
    private static readonly CampRoleSeed[] Seeds =
    [
        new("Consent Lead", SlotCount: 2, MinimumRequired: 1, SortOrder: 10, IsRequired: true),
        new("LNT", SlotCount: 1, MinimumRequired: 1, SortOrder: 20, IsRequired: true),
        new("Shit Ninja", SlotCount: 1, MinimumRequired: 1, SortOrder: 30, IsRequired: true),
        new("Power", SlotCount: 1, MinimumRequired: 0, SortOrder: 40, IsRequired: false),
        new("Build Lead", SlotCount: 2, MinimumRequired: 1, SortOrder: 50, IsRequired: true),
    ];

    private readonly ICampRoleService _campRoleService;

    public DevelopmentCampRoleSeeder(ICampRoleService campRoleService)
    {
        _campRoleService = campRoleService;
    }

    public async Task<DevelopmentCampRoleSeedResult> SeedAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var existing = await _campRoleService.ListDefinitionsAsync(includeDeactivated: true, ct);
        var existingNames = existing.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int created = 0;
        int skipped = 0;

        foreach (var seed in Seeds)
        {
            if (existingNames.Contains(seed.Name))
            {
                skipped++;
                continue;
            }

            await _campRoleService.CreateDefinitionAsync(
                new CreateCampRoleDefinitionInput(
                    Name: seed.Name,
                    Description: null,
                    SlotCount: seed.SlotCount,
                    MinimumRequired: seed.MinimumRequired,
                    SortOrder: seed.SortOrder,
                    IsRequired: seed.IsRequired),
                actorUserId,
                ct);
            created++;
        }

        return new DevelopmentCampRoleSeedResult(created, skipped);
    }

    private sealed record CampRoleSeed(string Name, int SlotCount, int MinimumRequired, int SortOrder, bool IsRequired);
}
