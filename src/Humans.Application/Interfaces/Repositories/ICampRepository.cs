using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the camps aggregate: <c>camps</c>, <c>camp_seasons</c>,
/// <c>camp_leads</c>, <c>camp_images</c>, <c>camp_historical_names</c>, and
/// <c>camp_settings</c>. The only non-test file that touches those DbSets
/// after the Camps migration lands.
/// </summary>
/// <remarks>
/// Reads are <c>AsNoTracking</c>. Mutating methods load tracked entities and
/// save changes atomically inside a single
/// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{HumansDbContext}"/>-owned
/// context so callers never have to reason about the EF context lifetime.
/// Cross-domain navigation (<c>CampLead.User</c>) is not resolved by this
/// repository; the application service stitches display names from
/// <see cref="Humans.Application.Interfaces.IUserService"/> per design-rules §6.
/// </remarks>
public interface ICampRepository
{
    // ==========================================================================
    // Reads — Camp
    // ==========================================================================

    /// <summary>
    /// Loads a camp with its seasons, active leads (FK-only; no
    /// <c>User</c> nav), historical names and images, by slug (case-insensitive).
    /// Returns null if not found. Read-only.
    /// </summary>
    Task<Camp?> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// Loads a camp with its seasons, active leads (FK-only), historical names
    /// and images, by id. Returns null if not found. Read-only.
    /// </summary>
    Task<Camp?> GetByIdAsync(Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Returns camps whose seasons for the year have status Active or Full,
    /// with seasons (year-filtered), images (sort-ordered), and historical
    /// names included. Read-only.
    /// </summary>
    Task<IReadOnlyList<Camp>> GetPublicCampsForYearAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Returns every camp that has any season for the year, with seasons
    /// (year-filtered), images and historical names included. Admin-only.
    /// </summary>
    Task<IReadOnlyList<Camp>> GetAllCampsForYearAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Returns camps participating in the year (any season), with seasons
    /// (year-filtered) and active leads included (FK-only). If
    /// <paramref name="statusFilter"/> is provided, only camps with at least
    /// one season in one of the given statuses are returned. Read-only.
    /// </summary>
    Task<IReadOnlyList<Camp>> GetCampsWithLeadsForYearAsync(
        int year,
        IReadOnlyList<CampSeasonStatus>? statusFilter,
        CancellationToken ct = default);

    /// <summary>
    /// Returns camps where the user has an active lead assignment, with
    /// seasons, images, and leads included (FK-only). Read-only.
    /// </summary>
    Task<IReadOnlyList<Camp>> GetCampsByLeadUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns count of seasons in Pending status across all years.
    /// </summary>
    Task<int> CountPendingSeasonsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns pending seasons with their parent <c>Camp</c> loaded (no
    /// cross-domain <c>User</c> nav on leads). Read-only.
    /// </summary>
    Task<IReadOnlyList<CampSeason>> GetPendingSeasonsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if <paramref name="slug"/> is already taken by any camp.
    /// </summary>
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);

    // ==========================================================================
    // Writes — Camp / Season / Lead (aggregate)
    // ==========================================================================

    /// <summary>
    /// Persist a new camp with its initial season, creator lead, and optional
    /// historical names in a single transaction.
    /// </summary>
    Task CreateCampAsync(
        Camp camp,
        CampSeason initialSeason,
        CampLead creatorLead,
        IReadOnlyList<CampHistoricalName>? historicalNames,
        CancellationToken ct = default);

    /// <summary>
    /// Updates mutable camp-level fields by id. Returns false if the camp was
    /// not found.
    /// </summary>
    Task<bool> UpdateCampFieldsAsync(
        Guid campId,
        string contactEmail,
        string contactPhone,
        string? webOrSocialUrl,
        IReadOnlyList<Domain.ValueObjects.CampLink>? links,
        bool isSwissCamp,
        int timesAtNowhere,
        bool hideHistoricalNames,
        Instant updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the set of distinct years any season exists for the camp.
    /// Used for cache invalidation.
    /// </summary>
    Task<IReadOnlyList<int>> GetCampYearsAsync(Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Delete a camp and all cascaded children (seasons, leads, images,
    /// historical names). Returns the storage paths of deleted images so the
    /// service can remove them from the filesystem. Returns null if the camp
    /// was not found.
    /// </summary>
    Task<IReadOnlyList<string>?> DeleteCampAsync(Guid campId, CancellationToken ct = default);

    // ==========================================================================
    // Writes — Season
    // ==========================================================================

    /// <summary>
    /// Load a season for mutation (tracked). Returns null if not found.
    /// </summary>
    Task<CampSeason?> GetSeasonForMutationAsync(Guid seasonId, CancellationToken ct = default);

    /// <summary>
    /// Save changes against a season previously loaded via
    /// <see cref="GetSeasonForMutationAsync"/>. The repository re-attaches the
    /// entity to a fresh context and commits.
    /// </summary>
    Task UpdateSeasonAsync(CampSeason season, CancellationToken ct = default);

    /// <summary>
    /// Returns true if a season exists for the given camp/year.
    /// </summary>
    Task<bool> SeasonExistsAsync(Guid campId, int year, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent season (by year desc) for the camp, read-only.
    /// </summary>
    Task<CampSeason?> GetLatestSeasonAsync(Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Returns true if any season for the camp has reached an approved status
    /// (Active, Full, or Withdrawn).
    /// </summary>
    Task<bool> HasApprovedSeasonAsync(Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Persist a new season. The service is responsible for populating all
    /// fields, including status.
    /// </summary>
    Task AddSeasonAsync(CampSeason season, CancellationToken ct = default);

    /// <summary>
    /// Sets the <c>NameLockDate</c> for every season in the given year.
    /// </summary>
    Task SetNameLockDateForYearAsync(int year, LocalDate lockDate, CancellationToken ct = default);

    /// <summary>
    /// Returns the maximum <c>NameLockDate</c> per year, for the given years.
    /// Missing years are absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<int, LocalDate?>> GetNameLockDatesAsync(
        IReadOnlyCollection<int> years,
        CancellationToken ct = default);

    // ==========================================================================
    // Cross-service queries (CampSeason by id)
    // ==========================================================================

    /// <summary>
    /// Returns the sound zone of the given season, read-only.
    /// </summary>
    Task<SoundZone?> GetSeasonSoundZoneAsync(Guid campSeasonId, CancellationToken ct = default);

    /// <summary>
    /// Returns the name of the given season, read-only.
    /// </summary>
    Task<string?> GetSeasonNameAsync(Guid campSeasonId, CancellationToken ct = default);

    /// <summary>
    /// Returns core (CampSeasonId, CampId, Year) info, read-only.
    /// </summary>
    Task<(Guid CampSeasonId, Guid CampId, int Year)?> GetSeasonInfoAsync(
        Guid campSeasonId, CancellationToken ct = default);

    /// <summary>
    /// Returns a tuple-shaped dictionary keyed by season id for seasons in the
    /// year, with (Name, CampSlug, SoundZone, SpaceRequirement). Read-only.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, (string Name, string CampSlug, SoundZone? SoundZone, SpaceSize? SpaceRequirement)>>
        GetSeasonDisplayDataForYearAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Returns brief (Id, Name, CampSlug, SpaceRequirement) rows for seasons
    /// in the year, ordered by name. Read-only.
    /// </summary>
    Task<IReadOnlyList<(Guid Id, string Name, string CampSlug, SpaceSize? SpaceRequirement)>>
        GetSeasonBriefsForYearAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Returns the season id where the user has an active lead assignment on
    /// a camp participating in the given year. Null if none.
    /// </summary>
    Task<Guid?> GetCampLeadSeasonIdForYearAsync(Guid userId, int year, CancellationToken ct = default);

    // ==========================================================================
    // Writes / reads — Lead
    // ==========================================================================

    /// <summary>
    /// Returns true if the user currently has an active lead assignment on
    /// the camp.
    /// </summary>
    Task<bool> IsUserActiveLeadAsync(Guid userId, Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of currently-active leads on the camp.
    /// </summary>
    Task<int> CountActiveLeadsAsync(Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Persist a new lead assignment.
    /// </summary>
    Task AddLeadAsync(CampLead lead, CancellationToken ct = default);

    /// <summary>
    /// Load a lead for mutation (tracked). Null if not found.
    /// </summary>
    Task<CampLead?> GetLeadForMutationAsync(Guid leadId, CancellationToken ct = default);

    /// <summary>
    /// Persist changes to a previously-loaded lead.
    /// </summary>
    Task UpdateLeadAsync(CampLead lead, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct set of user ids who currently have an active lead
    /// assignment on any camp. Used by <c>SystemTeamSyncJob</c> to sync the
    /// Barrio Leads team membership.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if the user currently leads any camp.
    /// </summary>
    Task<bool> IsLeadAnywhereAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all (read-only, AsNoTracking) lead assignments — active and
    /// historical — for the user, with parent <c>Camp</c> loaded for slug
    /// access. Used by the GDPR export contributor.
    /// </summary>
    Task<IReadOnlyList<CampLead>> GetAllLeadAssignmentsForUserAsync(
        Guid userId, CancellationToken ct = default);

    // ==========================================================================
    // Writes / reads — Historical names
    // ==========================================================================

    /// <summary>
    /// Persist a new historical name record.
    /// </summary>
    Task AddHistoricalNameAsync(CampHistoricalName historicalName, CancellationToken ct = default);

    /// <summary>
    /// Remove a historical name by id. Returns false if not found.
    /// </summary>
    Task<bool> RemoveHistoricalNameAsync(Guid historicalNameId, CancellationToken ct = default);

    // ==========================================================================
    // Writes / reads — Images
    // ==========================================================================

    /// <summary>
    /// Returns the count of images on the camp.
    /// </summary>
    Task<int> CountImagesAsync(Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Persist a new image record.
    /// </summary>
    Task AddImageAsync(CampImage image, CancellationToken ct = default);

    /// <summary>
    /// Load an image for mutation; null if not found.
    /// </summary>
    Task<CampImage?> GetImageForMutationAsync(Guid imageId, CancellationToken ct = default);

    /// <summary>
    /// Delete an image by id. Returns (StoragePath, CampId) if removed, or
    /// null if not found.
    /// </summary>
    Task<(string StoragePath, Guid CampId)?> DeleteImageAsync(Guid imageId, CancellationToken ct = default);

    /// <summary>
    /// Replace the sort order for the images on a camp using the provided
    /// id-ordered list. Images not in <paramref name="imageIdsInOrder"/> are
    /// left untouched.
    /// </summary>
    Task ReorderImagesAsync(
        Guid campId,
        IReadOnlyList<Guid> imageIdsInOrder,
        CancellationToken ct = default);

    // ==========================================================================
    // Writes / reads — Settings
    // ==========================================================================

    /// <summary>
    /// Returns the (singleton) camp settings row, read-only.
    /// </summary>
    Task<CampSettings?> GetSettingsReadOnlyAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets <c>PublicYear</c> on the singleton settings row.
    /// </summary>
    Task SetPublicYearAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="year"/> to <c>OpenSeasons</c> if not present.
    /// Returns true if the list changed.
    /// </summary>
    Task<bool> OpenSeasonAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Removes <paramref name="year"/> from <c>OpenSeasons</c>. Returns true
    /// if the list changed.
    /// </summary>
    Task<bool> CloseSeasonAsync(int year, CancellationToken ct = default);
}
