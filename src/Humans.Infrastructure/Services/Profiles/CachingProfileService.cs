using System.Collections.Concurrent;
using NodaTime;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Services.Profiles;

/// <summary>
/// Caching decorator for <see cref="IProfileService"/>. Handles:
/// <list type="bullet">
/// <item>Per-user FullProfile dict (ConcurrentDictionary, no TTL, replacing IMemoryCache + IProfileStore)</item>
/// <item>Cross-cutting cache invalidation on writes (nav badge, notification meter, etc.)</item>
/// </list>
/// All read methods pass through to the inner service.
/// All write methods delegate, then invalidate caches.
/// Write-through dict refresh lands in Phase 5.
/// </summary>
public sealed class CachingProfileService : IProfileService
{
    // Phase 3 cache-collapse note:
    //
    // The prior CachingProfileService called a local InvalidateUserCaches(userId)
    // helper on every write path, which invalidated three IMemoryCache entries:
    //
    //   - CacheKeys.UserProfile(userId)        : per-user Profile cache (2-min TTL)
    //   - CacheKeys.RoleAssignmentClaims(userId): role-claims cache
    //   - CacheKeys.ActiveTeams                : shared active-teams list
    //
    // None of these survive Phase 3 of the cache-collapse rework:
    //
    //   * UserProfile cache is gone entirely — GetProfileAsync is now a pure
    //     pass-through (the FullProfile dict is the canonical Profile cache).
    //   * RoleAssignmentClaims is owned by RoleAssignmentService; profile writes
    //     do not change role assignments, so the old call was defensive. Actual
    //     invalidation triggers (assign/end/revoke) are already handled by that
    //     service. Confirmed safe to drop at ~500-user scale.
    //   * ActiveTeams is owned by TeamService; profile writes do not change team
    //     membership. Same rationale — defensive call, real triggers covered by
    //     TeamService on membership mutations.
    //
    // The one genuine regression is documented inline on RequestDeletionAsync
    // (ShiftAuthorization, §15 NEW-B).

    private readonly IProfileService _inner;

    // Staged for Phase 5 write-through refresh.
    private readonly IProfileRepository _profileRepository;

    // Staged for Phase 5 write-through refresh.
    private readonly IUserService _userService;

    // Staged for Phase 5 write-through refresh.
    private readonly IUserEmailRepository _userEmailRepository;

    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly INotificationMeterCacheInvalidator _notificationMeter;

    private readonly ConcurrentDictionary<Guid, FullProfile> _byUserId = new();

    public CachingProfileService(
        IProfileService inner,
        IProfileRepository profileRepository,
        IUserService userService,
        IUserEmailRepository userEmailRepository,
        INavBadgeCacheInvalidator navBadge,
        INotificationMeterCacheInvalidator notificationMeter)
    {
        _inner = inner;
        _profileRepository = profileRepository;
        _userService = userService;
        _userEmailRepository = userEmailRepository;
        _navBadge = navBadge;
        _notificationMeter = notificationMeter;
    }

    // ==========================================================================
    // Reads — dict cache + pass-through
    // ==========================================================================

    // Pure pass-through: FullProfile dict (_byUserId) is the Profile cache now.
    // No separate IMemoryCache layer for raw Profile entities.
    public Task<Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default) =>
        _inner.GetProfileAsync(userId, ct);

    public ValueTask<FullProfile?> GetFullProfileAsync(Guid userId, CancellationToken ct = default)
    {
        if (_byUserId.TryGetValue(userId, out var hit))
            return new ValueTask<FullProfile?>(hit);

        return new ValueTask<FullProfile?>(LoadAndCacheAsync(userId, ct));
    }

    private async Task<FullProfile?> LoadAndCacheAsync(Guid userId, CancellationToken ct)
    {
        var result = await _inner.GetFullProfileAsync(userId, ct);
        if (result is not null)
            _byUserId[userId] = result;
        return result;
    }

    public Task<IReadOnlyDictionary<Guid, Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        _inner.GetByUserIdsAsync(userIds, ct);

    public Task<(Profile? Profile, MemberApplication? LatestApplication, int PendingConsentCount)>
        GetProfileIndexDataAsync(Guid userId, CancellationToken ct = default) =>
        _inner.GetProfileIndexDataAsync(userId, ct);

    public Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedCampaignGrantsAsync(
        Guid userId, CancellationToken ct = default) =>
        _inner.GetActiveOrCompletedCampaignGrantsAsync(userId, ct);

    public Task<(Profile? Profile, bool IsTierLocked, MemberApplication? PendingApplication)>
        GetProfileEditDataAsync(Guid userId, CancellationToken ct = default) =>
        _inner.GetProfileEditDataAsync(userId, ct);

    public Task<(byte[]? Data, string? ContentType)> GetProfilePictureAsync(
        Guid profileId, CancellationToken ct = default) =>
        _inner.GetProfilePictureAsync(profileId, ct);

    public Task<Instant?> GetEventHoldDateAsync(Guid userId, CancellationToken ct = default) =>
        _inner.GetEventHoldDateAsync(userId, ct);

    public Task<(int ColaboradorCount, int AsociadoCount)> GetTierCountsAsync(CancellationToken ct = default) =>
        _inner.GetTierCountsAsync(ct);

    public Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default) =>
        _inner.GetCustomPictureInfoByUserIdsAsync(userIds, ct);

    public Task<IReadOnlyList<Application.DTOs.BirthdayProfileInfo>>
        GetBirthdayProfilesAsync(int month, CancellationToken ct = default) =>
        _inner.GetBirthdayProfilesAsync(month, ct);

    public Task<IReadOnlyList<Application.DTOs.LocationProfileInfo>>
        GetApprovedProfilesWithLocationAsync(CancellationToken ct = default) =>
        _inner.GetApprovedProfilesWithLocationAsync(ct);

    public Task<IReadOnlyList<Application.DTOs.AdminHumanRow>> GetFilteredHumansAsync(
        string? search, string? statusFilter, CancellationToken ct = default) =>
        _inner.GetFilteredHumansAsync(search, statusFilter, ct);

    public Task<Application.DTOs.AdminHumanDetailData?> GetAdminHumanDetailAsync(
        Guid userId, CancellationToken ct = default) =>
        _inner.GetAdminHumanDetailAsync(userId, ct);

    public Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default) =>
        _inner.GetEmailCooldownInfoAsync(pendingEmailId, ct);

    public Task<IReadOnlyList<UserSearchResult>> SearchApprovedUsersAsync(
        string query, CancellationToken ct = default) =>
        _inner.SearchApprovedUsersAsync(query, ct);

    public Task<IReadOnlyList<HumanSearchResult>> SearchHumansAsync(
        string query, CancellationToken ct = default) =>
        _inner.SearchHumansAsync(query, ct);

    public Task<IReadOnlyList<ProfileLanguage>> GetProfileLanguagesAsync(
        Guid profileId, CancellationToken ct = default) =>
        _inner.GetProfileLanguagesAsync(profileId, ct);

    public async Task SaveProfileLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default)
    {
        await _inner.SaveProfileLanguagesAsync(profileId, languages, ct);
        // Write-through dict refresh (Phase 5) will handle dict invalidation here.
        // Nav-badge and notification-meter are unaffected by language changes.
    }

    public Task InvalidateCacheAsync(Guid userId, CancellationToken ct = default)
    {
        _byUserId.TryRemove(userId, out _);
        return Task.CompletedTask;
    }

    // ==========================================================================
    // Writes — delegate then invalidate
    // ==========================================================================

    public async Task SetMembershipTierAsync(
        Guid userId, MembershipTier tier, CancellationToken ct = default)
    {
        await _inner.SetMembershipTierAsync(userId, tier, ct);
        _byUserId.TryRemove(userId, out _);
        _navBadge.Invalidate();
    }

    public async Task<Guid> SaveProfileAsync(
        Guid userId, string displayName, ProfileSaveRequest request, string language,
        CancellationToken ct = default)
    {
        var result = await _inner.SaveProfileAsync(userId, displayName, request, language, ct);

        _navBadge.Invalidate();
        _notificationMeter.Invalidate();
        _byUserId.TryRemove(userId, out _);

        return result;
    }

    public async Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var result = await _inner.RequestDeletionAsync(userId, ct);
        if (result.Success)
        {
            _byUserId.TryRemove(userId, out _);
            // TODO(§15 NEW-B): ShiftAuthorization cache (shift-auth:{userId}, 60s TTL) is
            // no longer invalidated here. Tolerable at ~500-user scale given the short TTL
            // and that the user is being deleted. When Shifts migrates to the §15 pattern,
            // it should subscribe to IFullProfileInvalidator or equivalent to clear its
            // own cache on deletion.
        }
        return result;
    }

    public async Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var result = await _inner.CancelDeletionAsync(userId, ct);
        if (result.Success)
            _byUserId.TryRemove(userId, out _);
        return result;
    }

    public async Task SaveCVEntriesAsync(Guid userId, IReadOnlyList<CVEntry> entries, CancellationToken ct = default)
    {
        await _inner.SaveCVEntriesAsync(userId, entries, ct);
        _byUserId.TryRemove(userId, out _);
    }
}
