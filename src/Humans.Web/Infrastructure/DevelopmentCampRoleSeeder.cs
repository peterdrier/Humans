using Humans.Application.Interfaces.Camps;

namespace Humans.Web.Infrastructure;

public sealed class DevelopmentCampRoleSeeder(ICampRoleService campRoleService)
{
    private static readonly CreateCampRoleDefinitionInput[] Seeds =
    [
        new("Consent Lead", "consent-lead", null, SlotCount: 2, MinimumRequired: 1, SortOrder: 10),
        new("LNT", "lnt", null, SlotCount: 1, MinimumRequired: 1, SortOrder: 20),
        new("Shit Ninja", "shit-ninja", null, SlotCount: 1, MinimumRequired: 1, SortOrder: 30),
        new("Power", "power", null, SlotCount: 1, MinimumRequired: 0, SortOrder: 40),
        new("Build Lead", "build-lead", null, SlotCount: 2, MinimumRequired: 1, SortOrder: 50),
    ];

    public async Task<DevelopmentCampRoleSeedResult> SeedAsync(
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var existing = await campRoleService.ListDefinitionsAsync(includeDeactivated: true, cancellationToken);
        var existingNames = existing.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var skipped = 0;
        foreach (var input in Seeds)
        {
            if (existingNames.Contains(input.Name))
            {
                skipped++;
                continue;
            }

            await campRoleService.CreateDefinitionAsync(input, actorUserId, cancellationToken);
            created++;
        }

        return new DevelopmentCampRoleSeedResult(created, skipped);
    }
}

public sealed record DevelopmentCampRoleSeedResult(int Created, int Skipped)
{
    public string SuccessMessage => $"Camp roles seeded: {Created} created, {Skipped} already existed.";
}
