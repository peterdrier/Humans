namespace Humans.Camps.Contracts;

/// <summary>The camp writes the dev seeders need; the full write surface stays internal.</summary>
public interface ICampSeeding
{
    Task<Guid> CreateCampForSeedAsync(
        Guid createdByUserId,
        string name,
        string contactEmail,
        string contactPhone,
        bool isSwissCamp,
        int timesAtNowhere,
        CampSeasonData seasonData,
        int year,
        CancellationToken cancellationToken = default);

    Task OptInToSeasonAsync(Guid campId, int year, CancellationToken cancellationToken = default);

    Task ApproveSeasonAsync(Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Adds the human to the camp's active season and assigns the role. Idempotent.</summary>
    Task AddMemberAndAssignRoleInActiveSeasonAsync(
        Guid campId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default);
}

public interface ICampRoleSeeding
{
    Task<IReadOnlyList<CampRoleDefinitionSeedInfo>> ListDefinitionsForSeedAsync(
        bool includeDeactivated, CancellationToken cancellationToken = default);

    Task CreateDefinitionForSeedAsync(
        string name, string slug, string? description, int slotCount, int minimumRequired,
        int sortOrder, Guid actorUserId, CancellationToken cancellationToken = default);

    Task<int> SeedSystemRolesAsync(Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Null when no definition carries the slug.</summary>
    Task<Guid?> GetDefinitionIdBySlugAsync(string slug, CancellationToken cancellationToken = default);
}

public sealed record CampRoleDefinitionSeedInfo(Guid Id, string Slug, string Name);

public static class CampSeedRoles
{
    public const string CampLeadSlug = "camp-lead";
}
