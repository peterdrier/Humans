using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Camps.Services;

/// <summary>
/// Service for managing camps and camp-season state.
/// </summary>
internal interface ICampService : ICampServiceRead, IApplicationService
{
    // Registration
    Task<Camp> CreateCampAsync(
        Guid createdByUserId,
        string name,
        string contactEmail,
        string contactPhone,
        string? webOrSocialUrl,
        List<CampLink>? links,
        bool isSwissCamp,
        int timesAtNowhere,
        CampSeasonData seasonData,
        List<string>? historicalNames,
        int year,
        CancellationToken cancellationToken = default);

    // Queries
    Task<CampEditData?> GetCampEditDataAsync(
        Guid campId,
        int? preferredYear = null,
        CancellationToken cancellationToken = default);

    // Season management
    Task<CampSeason> OptInToSeasonAsync(Guid campId, int year, CancellationToken cancellationToken = default);
    Task UpdateSeasonAsync(Guid seasonId, CampSeasonData data, CancellationToken cancellationToken = default);
    Task ApproveSeasonAsync(Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default);
    Task RejectSeasonAsync(Guid seasonId, Guid reviewedByUserId, string notes, CancellationToken cancellationToken = default);
    Task WithdrawSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default);
    Task ReactivateSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default);
    // Camp updates
    Task<CampUpdateResult> UpdateCampAsync(CampUpdateInput input, CancellationToken cancellationToken = default);
    Task DeleteCampAsync(Guid campId, CancellationToken cancellationToken = default);

    // Historical names
    Task AddHistoricalNameAsync(Guid campId, string name, CancellationToken cancellationToken = default);
    Task RemoveHistoricalNameAsync(Guid historicalNameId, CancellationToken cancellationToken = default);

    // Images
    Task<CampImageUploadResult> UploadImageAsync(Guid campId, Stream fileStream, string fileName, string contentType, long length, CancellationToken cancellationToken = default);
    Task DeleteImageAsync(Guid imageId, CancellationToken cancellationToken = default);
    Task ReorderImagesAsync(Guid campId, List<Guid> imageIdsInOrder, CancellationToken cancellationToken = default);

    // Settings (CampAdmin)
    Task SetPublicYearAsync(int year, CancellationToken cancellationToken = default);
    Task OpenSeasonAsync(int year, CancellationToken cancellationToken = default);
    Task CloseSeasonAsync(int year, CancellationToken cancellationToken = default);
    Task SetNameLockDateAsync(int year, LocalDate lockDate, CancellationToken cancellationToken = default);

    // Name change (handles historical name logging)
    Task ChangeSeasonNameAsync(Guid seasonId, string newName, CancellationToken cancellationToken = default);

    // ==========================================================================
    // Camp membership per season (issue nobodies-collective#488)
    // ==========================================================================

    /// <summary>Idempotent — returns existing row's id with an <c>Already*</c> outcome if one exists.</summary>
    Task<CampMemberRequestResult> RequestCampMembershipAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Throws if the membership's season belongs to a different camp than <paramref name="scopedCampId"/>.</summary>
    Task ApproveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid approvedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Throws if the membership's season belongs to a different camp than <paramref name="scopedCampId"/>.</summary>
    Task RejectCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid rejectedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Throws if the membership's season belongs to a different camp than <paramref name="scopedCampId"/>.</summary>
    Task RemoveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid removedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Bypasses the request/approve flow for the camp's active season. Idempotent. Caller authorizes.</summary>
    Task<AddCampMemberOutcome> AddCampMemberToActiveSeasonAsync(
        Guid campId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the human as an active member of the season (idempotent — no-op if
    /// already active) and then assigns them the given camp role in a single
    /// operation. Used by the camp-edit role picker so callers don't have to
    /// orchestrate the two sub-mutations themselves. Caller authorizes.
    /// </summary>
    Task<AssignCampRoleOutcome> AddMemberAndAssignRoleInActiveSeasonAsync(
        Guid campId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Throws if <paramref name="userId"/> is not the row's owner.</summary>
    Task WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Returns failure if <paramref name="userId"/> is not the row's owner or the row cannot be left.</summary>
    Task<CampMembershipMutationResult> LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default);

    // ==========================================================================
    // Early Entry (issue nobodies-collective#490)
    // ==========================================================================

    /// <summary>
    /// Sets the global Early Entry start date in CampSettings. CampAdmin/Admin only;
    /// authorization enforced at the controller layer.
    /// </summary>
    Task SetEeStartDateAsync(
        LocalDate? eeStartDate, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the EE slot cap for a given camp season. CampAdmin/Admin only.
    /// Allowed to drop below the current granted-count: existing grants are retained
    /// but no new grants can be issued until the granted-count falls back under the cap.
    /// </summary>
    Task SetCampSeasonEeSlotCountAsync(
        Guid campSeasonId, int slotCount, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants or revokes Early Entry for a CampMember. Camp lead, CoLead, CampAdmin,
    /// or Admin only; authorization enforced at the controller layer.
    /// <paramref name="scopedCampId"/> must match the camp the member belongs to;
    /// returns MemberNotFound when the member does not exist or belongs to a different camp.
    /// Rejects when granting would push the season's active-granted count above
    /// CampSeason.EeSlotCount, or when the member is not Status=Active.
    /// Idempotent: writes no audit row when the value is already at the requested state.
    /// </summary>
    Task<SetEarlyEntryOutcome> SetEarlyEntryAsync(
        Guid scopedCampId, Guid campMemberId, bool granted, Guid actorUserId,
        CancellationToken cancellationToken = default);
}

internal sealed record CampMemberLookup(Guid CampSeasonId, Guid UserId, CampMemberStatus Status);

/// <summary>
/// Result of a camp membership request action.
/// </summary>
internal sealed record CampMemberRequestResult(
    Guid CampMemberId,
    CampMemberRequestOutcome Outcome,
    string Message,
    CampMemberRequestNoticeLevel NoticeLevel);

internal sealed record CampMembershipMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static CampMembershipMutationResult Success() => new(true, null);

    public static CampMembershipMutationResult Failure(string errorMessage) => new(false, errorMessage);
}

internal sealed record CampUpdateInput(
    Guid CampId,
    string ContactEmail,
    string ContactPhone,
    string? WebOrSocialUrl,
    List<CampLink>? Links,
    bool IsSwissCamp,
    int TimesAtNowhere,
    bool HideHistoricalNames,
    Guid SeasonId,
    string SeasonName,
    CampSeasonData SeasonData);

internal sealed record CampUpdateResult(bool Succeeded, string? ErrorMessage)
{
    public static CampUpdateResult Success() => new(true, null);

    public static CampUpdateResult Failure(string errorMessage) => new(false, errorMessage);
}

internal enum CampMemberRequestOutcome
{
    /// <summary>A new pending request was created.</summary>
    Created,
    /// <summary>An existing pending request already existed for the human.</summary>
    AlreadyPending,
    /// <summary>The human is already an active member of the camp for this season.</summary>
    AlreadyActive,
    /// <summary>No open season for the camp — the request was not created.</summary>
    NoOpenSeason
}

internal enum CampMemberRequestNoticeLevel
{
    Success,
    Info,
    Error
}

internal enum AddCampMemberOutcome
{
    Added,
    InvalidUser,
    NoActiveSeason
}

internal sealed record CampEditData(
    Guid CampId,
    string Slug,
    Guid SeasonId,
    int Year,
    bool IsNameLocked,
    string Name,
    string ContactEmail,
    string ContactPhone,
    IReadOnlyList<string> Links,
    bool IsSwissCamp,
    bool HideHistoricalNames,
    int TimesAtNowhere,
    string BlurbLong,
    string BlurbShort,
    string Languages,
    YesNoMaybe AcceptingMembers,
    YesNoMaybe KidsWelcome,
    KidsVisitingPolicy KidsVisiting,
    string? KidsAreaDescription,
    PerformanceSpaceStatus HasPerformanceSpace,
    string? PerformanceTypes,
    IReadOnlyList<CampVibe> Vibes,
    AdultPlayspacePolicy AdultPlayspace,
    int MemberCount,
    SpaceSize? SpaceRequirement,
    SoundZone? SoundZone,
    ElectricalGrid? ElectricalGrid,
    IReadOnlyList<CampImageSummary> Images,
    IReadOnlyList<CampHistoricalNameSummary> HistoricalNames);

internal sealed record CampImageUploadResult(bool Succeeded, CampImage? Image, string? ErrorMessage)
{
    public static CampImageUploadResult Success(CampImage image) => new(true, image, null);

    public static CampImageUploadResult Failure(string errorMessage) => new(false, null, errorMessage);
}

internal sealed record CampHistoricalNameSummary(
    Guid Id,
    string Name,
    int? Year,
    string Source);

internal sealed record CampPublicSummary(
    Guid Id,
    string Slug,
    string Name,
    string BlurbShort,
    string BlurbLong,
    string? ImageUrl,
    IReadOnlyList<string> Vibes,
    string AcceptingMembers,
    string KidsWelcome,
    string? SoundZone,
    string Status,
    int TimesAtNowhere,
    bool IsSwissCamp,
    IReadOnlyList<CampLink>? Links,
    string? WebOrSocialUrl);

internal sealed record CampPlacementSummary(
    Guid Id,
    string Slug,
    string Name,
    int MemberCount,
    string? SpaceRequirement,
    string? SoundZone,
    string Status,
    string? ElectricalGrid);

internal sealed record CampSeasonBrief(Guid CampSeasonId, string Name, string CampSlug, SpaceSize? SpaceRequirement);
