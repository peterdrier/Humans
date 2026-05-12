using Humans.Application.Architecture;
using Humans.Application.Interfaces;
using Humans.Application.DTOs;
using Humans.Application.Services.Camps;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Interfaces.Camps;

/// <summary>
/// Service for managing camps and camp-season state.
/// </summary>
/// <remarks>
/// Surface-budget recent history (newest first):
/// <list type="bullet">
///   <item>2026-05-11 — InterfaceMethodBudgetTests retired; budget migrated to [SurfaceBudget(55)] (issue nobodies-collective/Humans#700).</item>
///   <item>52→55 — issue-490 Early Entry (camps consumer): added SetEeStartDateAsync, SetCampSeasonEeSlotCountAsync, SetEarlyEntryAsync. Authorized by Peter 2026-05-10. EE state lives on CampSeason/CampMember/CampSettings — tables ICampService already owns — so the methods belong here per design-rules §6 (no service split).</item>
///   <item>51→52 — issue-682 global search: added SearchAsync(query, max). Authorized exception (Peter, 2026-05-09): queries against camps must live in the owning section per design-rules §6.</item>
///   <item>54→51 — barrio-mgmt-fixes audit (peterdrier#390). Net -3 after adding AddMemberAndAssignRoleAsync (+1) and removing 4 dead methods: GetCampDetailAsync, GetCampsByLeadUserIdAsync, SetSeasonFullAsync, GetCampSeasonBriefsForYearAsync.</item>
///   <item>55→54 — account-merge fold final consolidation: removed ReassignAssignmentsToUserAsync from ICampService (moved to IUserMerge.ReassignAsync, dispatched via fan-out).</item>
///   <item>55→55 — account-merge fold redesign Phase 3.3: added ReassignAssignmentsToUserAsync; removed GetCampByIdAsync (pure passthrough to ICampRepository.GetByIdAsync — zero production callers; CampDetail/Edit flows resolve by slug, not id).</item>
///   <item>56→55 — collapsed GetCampsForYearAsync + GetAllCampsForYearAsync into one method; callers filter via Camp.IsPublic predicate.</item>
///   <item>57→56 — simplify pass: added BuildCampDetailDataAsync, replaced 3 scoped CampSeason getters (SoundZone/Name/Info) with single GetCampSeasonByIdAsync.</item>
///   <item>53→57 — per-camp roles feature (peterdrier#489): AddCampMemberAsLeadAsync, GetSeasonMembersAsync, GetCampMemberStatusAsync, GetCampSeasonsForComplianceAsync — needed by ICampRoleService and the Camp Edit page roles panel.</item>
/// </list>
/// </remarks>
[SurfaceBudget(55)]
public interface ICampService : IApplicationService
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
    Task<CampLookup?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<CampDetailData?> BuildCampDetailDataBySlugAsync(
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
    /// <summary>
    /// Gets camps for a year without lead data.
    /// </summary>
    /// <remarks>
    /// <see cref="CampInfo.Leads"/> is always null; leads are not loaded by
    /// the year-directory query. Use <see cref="GetCampsWithLeadsForYearAsync"/>
    /// when lead data is needed.
    /// </remarks>
    Task<IReadOnlyList<CampInfo>> GetCampsForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampPublicSummary>> GetCampPublicSummariesForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampPlacementSummary>> GetCampPlacementSummariesForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Gets camps with their active leads (and lead user data) for a given year.
    /// Optionally filters to specific season statuses.
    /// </summary>
    /// <remarks>
    /// <see cref="CampInfo.Leads"/> is always a non-null list for results from
    /// this method.
    /// </remarks>
    Task<IReadOnlyList<CampInfo>> GetCampsWithLeadsForYearAsync(int year, IReadOnlyList<CampSeasonStatus>? statusFilter = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampSeasonInfo>> GetPendingSeasonsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Camps participating in the current public-year whose
    /// <c>CampSeason.Name</c> contains <paramref name="query"/>
    /// (case-insensitive). The public year is resolved from
    /// <c>CampSettings.PublicYear</c>. Only camps with a public-status
    /// season (<c>Active</c> / <c>Full</c>) for the year are surfaced —
    /// the same filter the public camp directory uses. Capped at
    /// <paramref name="max"/>; returned in unspecified order — the global
    /// search orchestrator scores and ranks. Used by the global /Search
    /// page (<c>SearchService</c>); every caller sees the public surface
    /// regardless of role.
    /// </summary>
    Task<IReadOnlyList<CampSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default);

    // Season management
    Task<CampSeason> OptInToSeasonAsync(Guid campId, int year, CancellationToken cancellationToken = default);
    Task UpdateSeasonAsync(Guid seasonId, CampSeasonData data, CancellationToken cancellationToken = default);
    Task ApproveSeasonAsync(Guid seasonId, Guid reviewedByUserId, string? notes, CancellationToken cancellationToken = default);
    Task RejectSeasonAsync(Guid seasonId, Guid reviewedByUserId, string notes, CancellationToken cancellationToken = default);
    Task WithdrawSeasonAsync(Guid seasonId, CancellationToken cancellationToken = default);
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
    Task<CampSeasonLookup?> GetCampSeasonByIdAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, CampSeasonDisplayData>> GetCampSeasonDisplayDataForYearAsync(int year, CancellationToken cancellationToken = default);
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

    /// <summary>Bypasses the request/approve flow. Idempotent. Caller authorizes.</summary>
    Task<Guid> AddCampMemberAsLeadAsync(Guid campSeasonId, Guid userId, Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the human as an active member of the season (idempotent — no-op if
    /// already active) and then assigns them the given camp role in a single
    /// operation. Used by the camp-edit role picker so callers don't have to
    /// orchestrate the two sub-mutations themselves. Caller authorizes.
    /// </summary>
    Task<AssignCampRoleOutcome> AddMemberAndAssignRoleAsync(
        Guid campSeasonId, Guid roleDefinitionId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Throws if <paramref name="userId"/> is not the row's owner.</summary>
    Task WithdrawCampMembershipRequestAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Throws if <paramref name="userId"/> is not the row's owner.</summary>
    Task LeaveCampAsync(
        Guid campMemberId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Returns <c>NoOpenSeason</c> if no Active/Full season exists for the public year.</summary>
    Task<CampMembershipState> GetMembershipStateForCampAsync(
        Guid campId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Privileged — caller must authorize.</summary>
    Task<CampMemberListData> GetCampMembersAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default);

    /// <summary>Raw rows (no display-name stitching, no lead union). Privileged — caller must authorize.</summary>
    Task<IReadOnlyList<CampSeasonMemberInfo>> GetSeasonMembersAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CampMembershipSummary>> GetCampMembershipsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);

    Task<int> GetPendingMembershipCountForLeadAsync(
        Guid userId, CancellationToken cancellationToken = default);

    Task<CampMemberLookup?> GetCampMemberStatusAsync(Guid campMemberId, CancellationToken ct = default);

    Task<IReadOnlyList<(Guid CampId, string CampName, string CampSlug, Guid CampSeasonId)>>
        GetCampSeasonsForComplianceAsync(int year, CancellationToken ct = default);

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

public sealed record CampMemberLookup(Guid CampSeasonId, Guid UserId, CampMemberStatus Status);

public sealed record CampLookup(
    Guid Id,
    string Slug,
    string ContactEmail,
    IReadOnlyList<CampSeasonInfo> Seasons,
    IReadOnlyList<CampLeadInfo> Leads);

public sealed record CampSettingsInfo(
    int PublicYear,
    IReadOnlyList<int> OpenSeasons,
    LocalDate? EeStartDate);

public sealed record CampSeasonLookup(
    Guid Id,
    Guid CampId,
    int Year,
    string Name,
    SoundZone? SoundZone);

public sealed record CampInfo(
    Guid Id,
    string Slug,
    string ContactEmail,
    string ContactPhone,
    bool IsSwissCamp,
    int TimesAtNowhere,
    IReadOnlyList<CampSeasonInfo> Seasons,
    /// <remarks>
    /// Null means leads were not loaded by <see cref="ICampService.GetCampsForYearAsync"/>.
    /// Results from <see cref="ICampService.GetCampsWithLeadsForYearAsync"/> always
    /// provide a non-null list, which may be empty when the camp has no leads.
    /// </remarks>
    IReadOnlyList<CampLeadInfo>? Leads);

public sealed record CampSeasonInfo(
    Guid Id,
    Guid CampId,
    string CampSlug,
    int Year,
    LocalDate? NameLockDate,
    string Name,
    string BlurbShort,
    string Languages,
    IReadOnlyList<CampVibe> Vibes,
    CampSeasonStatus Status,
    YesNoMaybe AcceptingMembers,
    YesNoMaybe KidsWelcome,
    AdultPlayspacePolicy AdultPlayspace,
    int MemberCount,
    SoundZone? SoundZone,
    SpaceSize? SpaceRequirement,
    int ContainerCount,
    ElectricalGrid? ElectricalGrid,
    int EeSlotCount,
    int? EeGrantedCount);

// CampLookup / CampInfo.Leads is populated only from camp loads that apply the
// filtered Include `.Include(c => c.Leads.Where(l => l.LeftAt == null))`, so
// every CampLeadInfo in scope is active. An IsActive field here would be
// invariantly true and misleading — leave it out.
public sealed record CampLeadInfo(Guid Id, Guid UserId);

public sealed record CampSeasonMemberInfo(
    Guid Id,
    Guid UserId,
    CampMemberStatus Status,
    Instant RequestedAt,
    Instant? ConfirmedAt,
    bool HasEarlyEntry);

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
    int EeSlotCount,
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
    bool IsLead,
    bool HasEarlyEntry);

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
    bool AcceptingMembers = false,
    string? Search = null);

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

public record CampSeasonDisplayData(string Name, string CampSlug, SoundZone? SoundZone, SpaceSize? SpaceRequirement);

public record CampSeasonBrief(Guid CampSeasonId, string Name, string CampSlug, SpaceSize? SpaceRequirement);
