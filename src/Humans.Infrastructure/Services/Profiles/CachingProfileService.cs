using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Caching;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Services.Profiles;

/// <summary>
/// Caching decorator for <see cref="IProfileService"/>. Handles:
/// <list type="bullet">
/// <item>Per-user profile cache (2-min TTL, replacing old CacheKeys.UserProfile)</item>
/// <item>Cross-cutting cache invalidation on writes (nav badge, notification meter, etc.)</item>
/// <item>Profile store updates after mutations</item>
/// </list>
/// All read methods pass through to the inner service.
/// All write methods delegate, then invalidate caches.
/// </summary>
public sealed class CachingProfileService : IProfileService
{
    private readonly IProfileService _inner;
    private readonly IProfileStore _store;
    private readonly IUserService _userService;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IMemoryCache _cache;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly INotificationMeterCacheInvalidator _notificationMeter;

    private static readonly TimeSpan ProfileCacheTtl = TimeSpan.FromMinutes(2);

    public CachingProfileService(
        IProfileService inner,
        IProfileStore store,
        IUserService userService,
        IUserEmailRepository userEmailRepository,
        IMemoryCache cache,
        INavBadgeCacheInvalidator navBadge,
        INotificationMeterCacheInvalidator notificationMeter)
    {
        _inner = inner;
        _store = store;
        _userService = userService;
        _userEmailRepository = userEmailRepository;
        _cache = cache;
        _navBadge = navBadge;
        _notificationMeter = notificationMeter;
    }

    // ==========================================================================
    // Reads — per-user cache + pass-through
    // ==========================================================================

    public async Task<Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.UserProfile(userId);
        if (_cache.TryGetExistingValue<Profile>(cacheKey, out var cached))
            return cached;

        var profile = await _inner.GetProfileAsync(userId, ct);
        if (profile is not null)
            _cache.Set(cacheKey, profile, ProfileCacheTtl);

        return profile;
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

    public Task SaveProfileLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default) =>
        _inner.SaveProfileLanguagesAsync(profileId, languages, ct);

    public async Task InvalidateCacheAsync(Guid userId, CancellationToken ct = default)
    {
        InvalidateUserCaches(userId);
        await RefreshStoreEntryAsync(userId, ct);
    }

    // ==========================================================================
    // Writes — delegate then invalidate
    // ==========================================================================

    public async Task SetMembershipTierAsync(
        Guid userId, MembershipTier tier, CancellationToken ct = default)
    {
        await _inner.SetMembershipTierAsync(userId, tier, ct);
        InvalidateUserCaches(userId);
        await RefreshStoreEntryAsync(userId, ct);
    }

    public async Task<Guid> SaveProfileAsync(
        Guid userId, string displayName, ProfileSaveRequest request, string language,
        CancellationToken ct = default)
    {
        var result = await _inner.SaveProfileAsync(userId, displayName, request, language, ct);

        _navBadge.Invalidate();
        _notificationMeter.Invalidate();
        InvalidateUserCaches(userId);
        await RefreshStoreEntryAsync(userId, ct);

        return result;
    }

    public async Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var result = await _inner.RequestDeletionAsync(userId, ct);
        if (result.Success)
        {
            _store.Remove(userId);
            _cache.InvalidateUserAccess(userId);
            InvalidateUserCaches(userId);
        }
        return result;
    }

    public async Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var result = await _inner.CancelDeletionAsync(userId, ct);
        if (result.Success)
        {
            InvalidateUserCaches(userId);
            await RefreshStoreEntryAsync(userId, ct);
        }
        return result;
    }

    // ==========================================================================
    // GDPR export pass-through
    // ==========================================================================

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        ((IUserDataContributor)_inner).ContributeForUserAsync(userId, ct);

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private void InvalidateUserCaches(Guid userId)
    {
        _cache.InvalidateUserProfile(userId);
        _cache.InvalidateRoleAssignmentClaims(userId);
        _cache.InvalidateActiveTeams();
    }

    private async Task RefreshStoreEntryAsync(Guid userId, CancellationToken ct)
    {
        var profile = await _inner.GetProfileAsync(userId, ct);
        if (profile is null)
        {
            _store.Remove(userId);
            return;
        }

        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null)
        {
            _store.Remove(userId);
            return;
        }

        var emails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);
        var notificationEmail = emails.FirstOrDefault(e => e.IsNotificationTarget && e.IsVerified)?.Email;

        _store.Upsert(userId, CachedProfile.Create(profile, user, notificationEmail));
    }
}
