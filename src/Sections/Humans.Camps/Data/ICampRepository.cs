using NodaTime;

namespace Humans.Camps.Data;

internal partial interface ICampRepository : IRepository
{
    /// <summary>Case-insensitive slug match. Leads are FK-only (no <c>User</c> nav).</summary>
    Task<Camp?> GetBySlugAsync(string slug, CancellationToken ct = default);

    Task<Camp?> GetByIdAsync(Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Returns camps participating in the year (any season), with seasons
    /// (year-filtered), members, images, historical names and active leads included (FK-only). If
    /// <paramref name="statusFilter"/> is provided, only camps with at least
    /// one season in one of the given statuses are returned. Read-only.
    /// </summary>
    Task<IReadOnlyList<Camp>> GetCampsWithLeadsForYearAsync(
        int year,
        IReadOnlyList<CampSeasonStatus>? statusFilter,
        CancellationToken ct = default);

    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);

    /// <summary>
    /// Persist a new camp with its initial season, the creator's Active CampMember,
    /// an optional Camp Lead role assignment (null when the Lead role definition is
    /// not yet seeded), and optional historical names in a single transaction.
    /// </summary>
    Task CreateCampAsync(
        Camp camp,
        CampSeason initialSeason,
        CampMember creatorMember,
        CampRoleAssignment? creatorLeadAssignment,
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
        IReadOnlyList<CampLink>? links,
        bool isSwissCamp,
        int timesAtNowhere,
        bool hideHistoricalNames,
        Instant updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Delete a camp and all cascaded children (seasons, leads, images,
    /// historical names). Returns the storage paths of deleted images so the
    /// service can remove them from the filesystem. Returns null if the camp
    /// was not found.
    /// </summary>
    Task<IReadOnlyList<string>?> DeleteCampAsync(Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Loads the season tracked inside a short-lived save context, applies
    /// <paramref name="mutate"/> to the tracked entity, and saves. EF's change
    /// tracker detects only the fields the lambda actually changed, so the
    /// <c>UPDATE</c> statement touches only those columns — unrelated concurrent
    /// edits to other fields are preserved. Returns false if not found.
    /// Validation that must block the write (e.g. wrong status) should throw
    /// from inside the lambda; the save is skipped when the exception bubbles.
    /// </summary>
    Task<bool> UpdateSeasonAsync(
        Guid seasonId,
        Action<CampSeason> mutate,
        CancellationToken ct = default);

    /// <summary>
    /// Atomic season rename: persists the mutation applied to the tracked
    /// season <em>and</em> the <see cref="CampHistoricalName"/> returned by
    /// <paramref name="mutate"/> in a single <c>SaveChangesAsync</c>. Return
    /// <c>null</c> from the lambda to signal a no-op (no history row added,
    /// season fields still saved if mutated). Throw from the lambda to block
    /// the save. Returns false if the season was not found.
    /// </summary>
    Task<bool> ApplyNameChangeAsync(
        Guid seasonId,
        Func<CampSeason, CampHistoricalName?> mutate,
        CancellationToken ct = default);

    Task<bool> SeasonExistsAsync(Guid campId, int year, CancellationToken ct = default);

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

    Task<int> SetNameLockDateForYearAsync(int year, LocalDate lockDate, CancellationToken ct = default);

    /// <summary>
    /// Returns the maximum <c>NameLockDate</c> per year, for the given years.
    /// Missing years are absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<int, LocalDate?>> GetNameLockDatesAsync(
        IReadOnlyCollection<int> years,
        CancellationToken ct = default);

    Task<CampSeason?> GetSeasonByIdAsync(Guid campSeasonId, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(CancellationToken ct = default);

    Task<bool> IsLeadAnywhereAsync(Guid userId, CancellationToken ct = default);

    Task AddHistoricalNameAsync(CampHistoricalName historicalName, CancellationToken ct = default);

    /// <summary>
    /// Remove a historical name by id. Returns false if not found.
    /// </summary>
    Task<bool> RemoveHistoricalNameAsync(Guid historicalNameId, CancellationToken ct = default);

    Task<int> CountImagesAsync(Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Persist a new image record after the camp's highest remaining display position.
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

    /// <summary>
    /// Returns the (singleton) camp settings row, read-only.
    /// </summary>
    Task<CampSettings?> GetSettingsReadOnlyAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CampEventSettings>> GetEventSettingsAsync(CancellationToken ct = default);

    /// <summary>Upserts the event's row. Returns true if the stored state changed (a new row counts as a change).</summary>
    Task<bool> SetRegistrationOpenAsync(Guid eventSettingsId, bool open, CancellationToken ct = default);

    /// <summary>Inserts only the events that have no row yet. Returns the rows inserted.</summary>
    Task<int> AddMissingEventSettingsAsync(IReadOnlyDictionary<Guid, bool> registrationOpenByEventId, CancellationToken ct = default);

    /// <summary>Empties the legacy <c>OpenSeasons</c> year list. Returns true if it held any year.</summary>
    Task<bool> ClearLegacyOpenSeasonsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CampSeason>> GetSeasonsWithoutEventIdAsync(CancellationToken ct = default);

    /// <summary>Only seasons still without an event id are written. Returns the rows written.</summary>
    Task<int> AssignSeasonEventIdsAsync(IReadOnlyDictionary<Guid, Guid> eventSettingsIdBySeasonId, CancellationToken ct = default);

    /// <summary>
    /// Idempotent insert of a Pending membership row. Handles the 23505
    /// unique-violation race on (CampSeasonId, UserId) by returning the
    /// winning row instead of throwing.
    /// </summary>
    Task<CampMemberInsertResult> RequestMembershipAsync(
        Guid campSeasonId, Guid userId, Instant requestedAt, CancellationToken ct = default);

    /// <summary>
    /// Idempotent insert of an Active membership row. If a matching Active row already
    /// exists, returns (existingId, AlreadyActive) without modification. If a Pending row
    /// exists, promotes it to Active and returns its id with Created. If no row exists,
    /// inserts a new Active row.
    /// </summary>
    Task<CampMemberInsertResult> AddActiveMembershipAsync(
        Guid campSeasonId, Guid userId, Instant now, Guid confirmedByUserId, CancellationToken ct = default);

    /// <summary>
    /// Loads a membership for a privileged mutation (approve/reject/remove),
    /// scoped to a specific camp. Returns null if not found, or if the
    /// membership's season belongs to a different camp than
    /// <paramref name="scopedCampId"/>. The returned entity is detached —
    /// callers mutate it and pass to <see cref="SaveMemberAsync"/>.
    /// </summary>
    Task<CampMember?> GetMemberForCampMutationAsync(
        Guid campMemberId, Guid scopedCampId, CancellationToken ct = default);

    /// <summary>
    /// Loads a membership for a self-mutation (withdraw/leave), scoped to a
    /// specific user. Returns null if not found, or if the membership's
    /// <c>UserId</c> does not match <paramref name="userId"/>. The returned
    /// entity is detached — callers mutate it and pass to
    /// <see cref="SaveMemberAsync"/>.
    /// </summary>
    Task<CampMember?> GetMemberForOwnMutationAsync(
        Guid campMemberId, Guid userId, CancellationToken ct = default);

    /// <summary>All of a user's memberships, across years and statuses, with camp/season loaded for GDPR export.</summary>
    Task<IReadOnlyList<CampMember>> GetAllMembershipsForUserAsync(Guid userId, CancellationToken ct = default);

    Task SaveMemberAsync(CampMember member, CancellationToken ct = default);

    /// <summary>
    /// Returns user ids of humans with a pending request for the given season.
    /// Used to notify requesters when a season is rejected or withdrawn. Does
    /// not mutate the membership rows — pending rows stay as-is so humans can
    /// re-request if the season is reactivated.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetPendingRequesterUserIdsForSeasonAsync(
        Guid campSeasonId, CancellationToken ct = default);

    /// <summary>Returns (CampSeasonId, UserId, Status) for the member, or null if not found. Read-only.</summary>
    Task<(Guid CampSeasonId, Guid UserId, CampMemberStatus Status)?> GetMemberLookupAsync(
        Guid campMemberId, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>EeSlotCount</c> on the given season. Returns (OldValue, NewValue, CampId),
    /// or null if the season was not found. When old == new, the row is not updated
    /// but the result tuple is still returned so the service can short-circuit the audit.
    /// </summary>
    Task<(int OldValue, int NewValue, Guid CampId)?> SetCampSeasonEeSlotCountAsync(
        Guid campSeasonId, int slotCount, CancellationToken cancellationToken = default);

    Task<int> GetGrantedCountForSeasonAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default);
}

internal enum CampMemberInsertOutcome
{
    /// <summary>A new pending row was inserted.</summary>
    Created,
    /// <summary>Row already existed in Pending status (includes races won by a rival insert).</summary>
    AlreadyPending,
    /// <summary>Row already existed in Active status.</summary>
    AlreadyActive
}

internal sealed record CampMemberInsertResult(Guid MemberId, CampMemberInsertOutcome Outcome);
