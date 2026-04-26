using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Interfaces.Camps;

public interface ICampService
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
    Task<Camp?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<Camp?> GetCampByIdAsync(Guid campId, CancellationToken cancellationToken = default);
    Task<CampDetailData?> GetCampDetailAsync(
        string slug,
        int? preferredYear = null,
        bool fallbackToLatestSeason = true,
        CancellationToken cancellationToken = default);
    Task<CampEditData?> GetCampEditDataAsync(
        Guid campId,
        int? preferredYear = null,
        CancellationToken cancellationToken = default);
    Task<CampDirectoryResult> GetCampDirectoryAsync(
        Guid? userId,
        CampDirectoryFilter? filter = null,
        CancellationToken cancellationToken = default);
    Task<List<Camp>> GetCampsForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<List<Camp>> GetAllCampsForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampPublicSummary>> GetCampPublicSummariesForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampPlacementSummary>> GetCampPlacementSummariesForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<CampSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Gets camps with their active leads (and lead user data) for a given year.
    /// Optionally filters to specific season statuses.
    /// </summary>
    Task<List<Camp>> GetCampsWithLeadsForYearAsync(int year, IReadOnlyList<CampSeasonStatus>? statusFilter = null, CancellationToken cancellationToken = default);
    Task<List<Camp>> GetCampsByLeadUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<List<CampSeason>> GetPendingSeasonsAsync(CancellationToken cancellationToken = default);

    // Season management
    Task<CampSeason> OptInToSeasonAsync(Guid campId, int year, CancellationToken cancellationToken = default);
    Task UpdateSeasonAsync(Guid seasonId, CampSeasonData data, CancellationToken cancellationToken = default);
    Task ApproveSeasonAsync(Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default);
    Task RejectSeasonAsync(Guid seasonId, Guid reviewedByUserId, string notes, CancellationToken cancellationToken = default);
    Task WithdrawSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default);
    Task SetSeasonFullAsync(Guid seasonId, CancellationToken cancellationToken = default);
    Task ReactivateSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default);
    // Camp updates
    Task UpdateCampAsync(Guid campId, string contactEmail, string contactPhone,
        string? webOrSocialUrl, List<CampLink>? links, bool isSwissCamp, int timesAtNowhere,
        bool hideHistoricalNames,
        CancellationToken cancellationToken = default);
    Task DeleteCampAsync(Guid campId, CancellationToken cancellationToken = default);

    // Lead management
    Task<CampLead> AddLeadAsync(Guid campId, Guid userId, CancellationToken cancellationToken = default);
    Task RemoveLeadAsync(Guid leadId, CancellationToken cancellationToken = default);

    // Historical names
    Task AddHistoricalNameAsync(Guid campId, string name, CancellationToken cancellationToken = default);
    Task RemoveHistoricalNameAsync(Guid historicalNameId, CancellationToken cancellationToken = default);

    // Cross-service queries (used by CityPlanningService)
    Task<SoundZone?> GetCampSeasonSoundZoneAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<string?> GetCampSeasonNameAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<CampSeasonInfo?> GetCampSeasonInfoAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, CampSeasonDisplayData>> GetCampSeasonDisplayDataForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampSeasonBrief>> GetCampSeasonBriefsForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<Guid?> GetCampLeadSeasonIdForYearAsync(Guid userId, int year, CancellationToken cancellationToken = default);

    // Authorization checks
    Task<bool> IsUserCampLeadAsync(Guid userId, Guid campId, CancellationToken cancellationToken = default);

    // Images
    Task<CampImage> UploadImageAsync(Guid campId, Stream fileStream, string fileName, string contentType, long length, CancellationToken cancellationToken = default);
    Task DeleteImageAsync(Guid imageId, CancellationToken cancellationToken = default);
    Task ReorderImagesAsync(Guid campId, List<Guid> imageIdsInOrder, CancellationToken cancellationToken = default);

    // Settings (CampAdmin)
    Task SetPublicYearAsync(int year, CancellationToken cancellationToken = default);
    Task OpenSeasonAsync(int year, CancellationToken cancellationToken = default);
    Task CloseSeasonAsync(int year, CancellationToken cancellationToken = default);
    Task SetNameLockDateAsync(int year, LocalDate lockDate, CancellationToken cancellationToken = default);
    Task<Dictionary<int, LocalDate?>> GetNameLockDatesAsync(List<int> years, CancellationToken cancellationToken = default);

    // Name change (handles historical name logging)
    Task ChangeSeasonNameAsync(Guid seasonId, string newName, CancellationToken cancellationToken = default);

    // ==========================================================================
    // Camp membership per season (issue nobodies-collective#488)
    // ==========================================================================

    /// <summary>
    /// Human requests to join a camp for the currently-open season. No-op if
    /// an active or pending row already exists — the existing row's id is
    /// returned with an <c>Already*</c> outcome. Handles concurrent duplicate
    /// submissions idempotently.
    /// </summary>
    Task<CampMemberRequestResult> RequestCampMembershipAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lead / CampAdmin approves a pending request. Scoped to the authorizing
    /// camp: throws if the membership's season belongs to a different camp.
    /// Sends <c>CampMembershipApproved</c> notification to the requester.
    /// </summary>
    Task ApproveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid approvedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lead / CampAdmin rejects a pending request. Scoped to the authorizing
    /// camp: throws if the membership's season belongs to a different camp.
    /// Sends <c>CampMembershipRejected</c> notification to the requester.
    /// </summary>
    Task RejectCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid rejectedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lead / CampAdmin removes an active member. Scoped to the authorizing camp.
    /// </summary>
    Task RemoveCampMemberAsync(
        Guid scopedCampId, Guid campMemberId, Guid removedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lead-driven add: creates a CampMember with Status=Active directly, bypassing
    /// the request/approve flow. Used by the "Add active member" button on the camp Edit
    /// page. Idempotent — if an Active membership already exists, returns its id without
    /// auditing again. Authorization is the caller's responsibility (CampOperationRequirement.Manage).
    /// </summary>
    Task<Guid> AddCampMemberAsLeadAsync(Guid campSeasonId, Guid userId, Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Human withdraws their own pending request. Scoped to the caller's user id.
    /// </summary>
    Task WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Human leaves their own active membership. Scoped to the caller's user id.
    /// </summary>
    Task LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current user's membership state for a camp's open-season
    /// (or <c>NoOpenSeason</c> if the camp has no Active/Full season this year).
    /// </summary>
    Task<CampMembershipState> GetMembershipStateForCampAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists Pending + Active members for a given camp season, stitched with
    /// display names via <c>IUserService</c>. Privileged view (no authorization
    /// inside — caller must gate).
    /// </summary>
    Task<CampMemberListData> GetCampMembersAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the raw <see cref="CampMember"/> rows (Active + Pending) for a
    /// given camp season — no display-name stitching, no lead union. Used by
    /// the Camp Edit page to build the per-camp roles picker (which needs
    /// CampMember.Id and UserId for active members only). Privileged view
    /// (no authorization inside — caller must gate).
    /// </summary>
    Task<IReadOnlyList<CampMember>> GetSeasonMembersAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists Active/Pending memberships for a human, grouped by year. Used for
    /// the human's own profile dashboard (MyCamps).
    /// </summary>
    Task<IReadOnlyList<CampMembershipSummary>> GetCampMembershipsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Total number of Pending membership requests across all camps where the
    /// user is an active lead. Used by the notification meter to show a single
    /// "N people want to join your camp" counter instead of emitting a stored
    /// notification per request.
    /// </summary>
    Task<int> GetPendingMembershipCountForLeadAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the season + status + user of a camp member, or null if not found.
    /// Used by ICampRoleService to enforce the active-member-in-correct-season precondition.
    /// </summary>
    Task<CampMemberLookup?> GetCampMemberStatusAsync(Guid campMemberId, CancellationToken ct = default);

    /// <summary>
    /// Returns (CampId, CampName, CampSlug, CampSeasonId) tuples for every camp that has
    /// any season in the given year. Used by the camp role compliance report.
    /// </summary>
    Task<IReadOnlyList<(Guid CampId, string CampName, string CampSlug, Guid CampSeasonId)>>
        GetCampSeasonsForComplianceAsync(int year, CancellationToken ct = default);
}

public sealed record CampMemberLookup(Guid CampSeasonId, Guid UserId, CampMemberStatus Status);

/// <summary>
/// Result of a camp membership request action.
/// </summary>
public record CampMemberRequestResult(
    Guid CampMemberId,
    CampMemberRequestOutcome Outcome,
    string? Message = null);

public enum CampMemberRequestOutcome
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

/// <summary>
/// Current human's camp membership state relative to a camp's open-season.
/// </summary>
public record CampMembershipState(
    int? OpenSeasonYear,
    Guid? OpenSeasonId,
    Guid? CampMemberId,
    CampMemberStatusSummary Status);

public enum CampMemberStatusSummary
{
    /// <summary>No open season for the camp.</summary>
    NoOpenSeason,
    /// <summary>Open season exists but human has no record.</summary>
    None,
    /// <summary>Human has a pending request.</summary>
    Pending,
    /// <summary>Human is an active member.</summary>
    Active
}

public record CampMemberListData(
    Guid CampSeasonId,
    int Year,
    IReadOnlyList<CampMemberRow> Pending,
    IReadOnlyList<CampMemberRow> Active);

// TEMP: `IsLead` is a display-only flag populated by unioning active `CampLead`
// rows into the active-members list. It'll be superseded by the upcoming camp
// roles PR (Team-style role assignments on CampMember), which will subsume the
// CampLead concept entirely. Remove this flag + the union logic in
// CampService.GetCampMembersAsync when that lands.
public record CampMemberRow(
    Guid CampMemberId,
    Guid UserId,
    string DisplayName,
    Instant RequestedAt,
    Instant? ConfirmedAt,
    bool IsLead);

public record CampMembershipSummary(
    Guid CampMemberId,
    Guid CampId,
    string CampSlug,
    string CampName,
    Guid CampSeasonId,
    int Year,
    CampMemberStatus Status,
    Instant RequestedAt,
    Instant? ConfirmedAt);

public record CampSeasonData(
    string BlurbLong,
    string BlurbShort,
    string Languages,
    YesNoMaybe AcceptingMembers,
    YesNoMaybe KidsWelcome,
    KidsVisitingPolicy KidsVisiting,
    string? KidsAreaDescription,
    PerformanceSpaceStatus HasPerformanceSpace,
    string? PerformanceTypes,
    List<CampVibe> Vibes,
    AdultPlayspacePolicy AdultPlayspace,
    int MemberCount,
    SpaceSize? SpaceRequirement,
    SoundZone? SoundZone,
    int ContainerCount,
    string? ContainerNotes,
    ElectricalGrid? ElectricalGrid);

public record CampDirectoryFilter(
    CampVibe? Vibe = null,
    SoundZone? SoundZone = null,
    bool KidsFriendly = false,
    bool AcceptingMembers = false);

public record CampDirectoryCard(
    Guid Id,
    string Slug,
    string Name,
    string BlurbShort,
    string? ImageUrl,
    IReadOnlyList<CampVibe> Vibes,
    YesNoMaybe AcceptingMembers,
    YesNoMaybe KidsWelcome,
    SoundZone? SoundZone,
    CampSeasonStatus Status,
    int TimesAtNowhere);

public record CampDirectoryResult(
    int Year,
    int PendingCount,
    IReadOnlyList<CampDirectoryCard> Camps,
    IReadOnlyList<CampDirectoryCard> MyCamps);

public record CampDetailData(
    Guid Id,
    string Slug,
    string Name,
    IReadOnlyList<CampLink> Links,
    bool IsSwissCamp,
    int TimesAtNowhere,
    bool HideHistoricalNames,
    IReadOnlyList<string> HistoricalNames,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<CampLeadSummary> Leads,
    CampSeasonDetailData? CurrentSeason);

public record CampLeadSummary(
    Guid LeadId,
    Guid UserId,
    string DisplayName);

public record CampEditData(
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
    int ContainerCount,
    string? ContainerNotes,
    ElectricalGrid? ElectricalGrid,
    IReadOnlyList<CampLeadSummary> Leads,
    IReadOnlyList<CampImageSummary> Images,
    IReadOnlyList<CampHistoricalNameSummary> HistoricalNames);

public record CampImageSummary(
    Guid Id,
    string Url,
    int SortOrder);

public record CampHistoricalNameSummary(
    Guid Id,
    string Name,
    int? Year,
    string Source);

public record CampSeasonDetailData(
    Guid Id,
    int Year,
    string Name,
    CampSeasonStatus Status,
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
    int ContainerCount,
    string? ContainerNotes,
    ElectricalGrid? ElectricalGrid,
    bool IsNameLocked);

public record CampPublicSummary(
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

public record CampPlacementSummary(
    Guid Id,
    string Slug,
    string Name,
    int MemberCount,
    string? SpaceRequirement,
    string? SoundZone,
    int ContainerCount,
    string? ContainerNotes,
    string Status,
    string? ElectricalGrid);

/// <summary>
/// Core camp season info for cross-service lookups (CampId + Year).
/// </summary>
public record CampSeasonInfo(Guid CampSeasonId, Guid CampId, int Year);

/// <summary>
/// Display data for camp seasons: name, camp slug, and sound zone.
/// Used by CityPlanningService for polygon display/export.
/// </summary>
public record CampSeasonDisplayData(string Name, string CampSlug, SoundZone? SoundZone, SpaceSize? SpaceRequirement);

/// <summary>
/// Lightweight camp season summary (ID, name, camp slug) for listing.
/// </summary>
public record CampSeasonBrief(Guid CampSeasonId, string Name, string CampSlug, SpaceSize? SpaceRequirement);
