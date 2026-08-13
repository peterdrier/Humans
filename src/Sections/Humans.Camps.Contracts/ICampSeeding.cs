namespace Humans.Camps.Contracts;

/// <summary>
/// The camp write verbs <c>Humans.Development</c>'s dev seeders drive.
///
/// <para>
/// Budget's rule (one leaf method per Shell seeder) does not apply here and neither does
/// taking the seeding in-house: <c>DevPersonaSeeder</c> and <c>DevelopmentCampRoleSeeder</c>
/// build <b>multi-section</b> persona fixtures, so moving them into this section would steal
/// another section's fixtures (Teams' <c>ITeamSeeding</c> shape). The section's full write
/// surface — approve/reject/withdraw seasons, images, historical names, membership and early
/// entry — stays internal; only the verbs a fixture actually needs are here.
/// </para>
/// </summary>
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

/// <summary>
/// The camp-role definition verbs the dev seeders drive. Separate from
/// <see cref="ICampSeeding"/> because <c>DevelopmentCampRoleSeeder</c> needs only these and
/// the two services behind them are separate (design §15 step 5b: the interface count is not
/// the thing to minimise, the surface is).
/// </summary>
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

/// <summary>Flat projection of a camp role definition, for seeders deciding what already exists.</summary>
public sealed record CampRoleDefinitionSeedInfo(Guid Id, string Slug, string Name);

/// <summary>The system camp-role slugs a fixture may name.</summary>
public static class CampSeedRoles
{
    public const string CampLeadSlug = "camp-lead";
}
