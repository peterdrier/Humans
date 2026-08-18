using Humans.Camps.Contracts;

namespace Humans.Development.Services;

internal sealed class DevelopmentCampRoleSeeder(ICampRoleSeeding campRoleSeeding)
{
    // The section's CreateCampRoleDefinitionInput turned internal at Camps' G5; the leaf takes
    // the fields instead, so the fixture is a local shape (Finance's "own the boundary type").
    private sealed record Seed(string Name, string Slug, int SlotCount, int MinimumRequired, int SortOrder);

    private static readonly Seed[] Seeds =
    [
        new("Consent Lead", "consent-lead", SlotCount: 2, MinimumRequired: 1, SortOrder: 10),
        new("LNT", "lnt", SlotCount: 1, MinimumRequired: 1, SortOrder: 20),
        new("Shit Ninja", "shit-ninja", SlotCount: 1, MinimumRequired: 1, SortOrder: 30),
        new("Power", "power", SlotCount: 1, MinimumRequired: 0, SortOrder: 40),
        new("Build Lead", "build-lead", SlotCount: 2, MinimumRequired: 1, SortOrder: 50),
    ];

    public async Task<DevelopmentCampRoleSeedResult> SeedAsync(
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var existing = await campRoleSeeding.ListDefinitionsForSeedAsync(includeDeactivated: true, cancellationToken);
        var existingNames = existing.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var skipped = 0;
        foreach (var seed in Seeds)
        {
            if (existingNames.Contains(seed.Name))
            {
                skipped++;
                continue;
            }

            await campRoleSeeding.CreateDefinitionForSeedAsync(
                seed.Name, seed.Slug, description: null, seed.SlotCount, seed.MinimumRequired,
                seed.SortOrder, actorUserId, cancellationToken);
            created++;
        }

        return new DevelopmentCampRoleSeedResult(created, skipped);
    }
}

internal sealed record DevelopmentCampRoleSeedResult(int Created, int Skipped)
{
    public string SuccessMessage => $"Camp roles seeded: {Created} created, {Skipped} already existed.";
}
