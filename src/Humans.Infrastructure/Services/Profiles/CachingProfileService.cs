using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;
using ProfilesProfileService = Humans.Application.Services.Profile.ProfileService;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Infrastructure.Services.Profiles;

/// <summary>
/// Singleton caching decorator for <see cref="IProfileService"/>. Owns a private
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> of <see cref="FullProfile"/>
/// entries keyed by userId. Reads serve dict hits synchronously via
/// <see cref="ValueTask{TResult}"/>; writes reload the affected entry from
/// repositories via <c>RefreshEntryAsync</c>.
///
/// <para>
/// Registered as Singleton so the dict persists across requests. Dependencies
/// that are themselves Scoped (<see cref="IProfileService"/> inner service,
/// <see cref="IUserService"/>, <see cref="INavBadgeCacheInvalidator"/>,
/// <see cref="INotificationMeterCacheInvalidator"/>) are resolved per-call via
/// <see cref="IServiceScopeFactory"/> to avoid the captured-scoped-dependency
/// anti-pattern.
/// </para>
///
/// <para>
/// <see cref="IProfileRepository"/> and <see cref="IUserEmailRepository"/> are
/// injected directly because they are also Singleton (IDbContextFactory-based).
/// </para>
/// </summary>
public sealed class CachingProfileService : IProfileService, IFullProfileInvalidator
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

    /// <summary>
    /// DI service key under which the undecorated (inner) <see cref="IProfileService"/>
    /// is registered. Used by the Singleton decorator to resolve the Scoped inner
    /// service per-call without triggering self-resolution on the unkeyed
    /// <see cref="IProfileService"/> registration (which maps to this Singleton).
    /// </summary>
    public const string InnerServiceKey = "profile-inner";

    private readonly IProfileRepository _profileRepository;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly ConcurrentDictionary<Guid, FullProfile> _byUserId = new();

    public CachingProfileService(
        IProfileRepository profileRepository,
        IUserEmailRepository userEmailRepository,
        IServiceScopeFactory scopeFactory)
    {
        _profileRepository = profileRepository;
        _userEmailRepository = userEmailRepository;
        _scopeFactory = scopeFactory;
    }

    // ==========================================================================
    // Reads — dict cache + pass-through
    // ==========================================================================

    // Pure pass-through: FullProfile dict (_byUserId) is the Profile cache now.
    // No separate IMemoryCache layer for raw Profile entities.
    public async Task<Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetProfileAsync(userId, ct);
    }

    public ValueTask<FullProfile?> GetFullProfileAsync(Guid userId, CancellationToken ct = default)
    {
        if (_byUserId.TryGetValue(userId, out var hit))
            return new ValueTask<FullProfile?>(hit);

        return new ValueTask<FullProfile?>(LoadAndCacheAsync(userId, ct));
    }

    private async Task<FullProfile?> LoadAndCacheAsync(Guid userId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var result = await inner.GetFullProfileAsync(userId, ct);
        if (result is not null)
            _byUserId[userId] = result;
        return result;
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    /// <summary>
    /// Reloads the <see cref="FullProfile"/> for <paramref name="userId"/> directly
    /// from repositories and upserts it in <see cref="_byUserId"/>.
    /// If the profile or user no longer exists, the entry is removed instead.
    /// Called after every mutation so the dict stays consistent without eviction.
    /// </summary>
    private async Task RefreshEntryAsync(Guid userId, CancellationToken ct)
    {
        // If any repository call throws, the dict retains the pre-mutation entry;
        // the next cache miss will re-load from the inner service. This is tolerable
        // at single-server ~500-user scale — the surface area for a divergence
        // window is a single process' lifetime.
        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);
        if (profile is null)
        {
            _byUserId.TryRemove(userId, out _);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        var user = await userService.GetByIdAsync(userId, ct);
        if (user is null)
        {
            _byUserId.TryRemove(userId, out _);
            return;
        }

        var userEmails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);
        var notificationEmail = userEmails.FirstOrDefault(e => e.IsNotificationTarget && e.IsVerified)?.Email
                                ?? user.Email;

        _byUserId[userId] = FullProfile.Create(profile, user, profile.VolunteerHistory.ToList(), notificationEmail);
    }

    /// <summary>
    /// Populates <see cref="_byUserId"/> with a <see cref="FullProfile"/> for every
    /// existing profile. Called at application startup by
    /// <c>FullProfileWarmupHostedService</c> so bulk-read methods
    /// (<see cref="GetBirthdayProfilesAsync"/>,
    /// <see cref="GetApprovedProfilesWithLocationAsync"/>,
    /// <see cref="GetFilteredHumansAsync"/>,
    /// <see cref="SearchApprovedUsersAsync"/>) return complete results immediately
    /// after deploy rather than filling in lazily as each user is accessed.
    /// </summary>
    /// <remarks>
    /// Trivial at ~500-user scale. Runs inside an ad-hoc DI scope so the Scoped
    /// <see cref="IUserService"/> can be resolved. Exceptions propagate to the
    /// caller; the hosted service logs and swallows so startup is never blocked
    /// by a warmup failure — lazy population from <see cref="GetFullProfileAsync"/>
    /// will still work.
    /// </remarks>
    public async Task WarmAllAsync(CancellationToken ct = default)
    {
        var profiles = await _profileRepository.GetAllAsync(ct);
        if (profiles.Count == 0)
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        var userIds = profiles.Select(p => p.UserId).ToList();
        var users = await userService.GetByIdsAsync(userIds, ct);
        var notificationEmails = await _userEmailRepository.GetAllNotificationTargetEmailsAsync(ct);

        foreach (var profile in profiles)
        {
            if (!users.TryGetValue(profile.UserId, out var user))
                continue;

            notificationEmails.TryGetValue(profile.UserId, out var notificationEmail);
            _byUserId[profile.UserId] =
                FullProfile.Create(profile, user, profile.VolunteerHistory.ToList(), notificationEmail);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetByUserIdsAsync(userIds, ct);
    }

    public async Task<(Profile? Profile, MemberApplication? LatestApplication, int PendingConsentCount)>
        GetProfileIndexDataAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetProfileIndexDataAsync(userId, ct);
    }

    public async Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedCampaignGrantsAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetActiveOrCompletedCampaignGrantsAsync(userId, ct);
    }

    public async Task<(Profile? Profile, bool IsTierLocked, MemberApplication? PendingApplication)>
        GetProfileEditDataAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetProfileEditDataAsync(userId, ct);
    }

    public async Task<(byte[]? Data, string? ContentType)> GetProfilePictureAsync(
        Guid profileId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetProfilePictureAsync(profileId, ct);
    }

    public async Task<Instant?> GetEventHoldDateAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetEventHoldDateAsync(userId, ct);
    }

    public async Task<(int ColaboradorCount, int AsociadoCount)> GetTierCountsAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetTierCountsAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetActiveApprovedUserIdsAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetActiveApprovedUserIdsAsync(ct);
    }

    public async Task<int> GetActiveApprovedCountAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetActiveApprovedCountAsync(ct);
    }

    public async Task<int> GetConsentReviewPendingCountAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetConsentReviewPendingCountAsync(ct);
    }

    public async Task<int> GetNotApprovedAndNotSuspendedCountAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetNotApprovedAndNotSuspendedCountAsync(ct);
    }

    public async Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetCustomPictureInfoByUserIdsAsync(userIds, ct);
    }

    public Task<IReadOnlyList<Application.DTOs.BirthdayProfileInfo>>
        GetBirthdayProfilesAsync(int month, CancellationToken ct = default)
    {
        return Task.FromResult(
            ProfilesProfileService.GetBirthdayProfilesFromSnapshot(_byUserId.Values, month));
    }

    public Task<IReadOnlyList<Application.DTOs.LocationProfileInfo>>
        GetApprovedProfilesWithLocationAsync(CancellationToken ct = default)
    {
        return Task.FromResult(
            ProfilesProfileService.GetApprovedProfilesWithLocationFromSnapshot(_byUserId.Values));
    }

    public async Task<IReadOnlyList<Application.DTOs.AdminHumanRow>> GetFilteredHumansAsync(
        string? search, string? statusFilter, CancellationToken ct = default)
    {
        // Snapshot the dict values once to avoid holding the live reference across awaits.
        var snapshot = _byUserId.Values.ToList();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        var membershipCalculator = scope.ServiceProvider.GetRequiredService<IMembershipCalculator>();

        var allUsers = await userService.GetAllUsersAsync(ct);
        return await ProfilesProfileService.GetFilteredHumansFromSnapshotAsync(
            snapshot, search, statusFilter, allUsers, membershipCalculator, ct);
    }

    public async Task<Application.DTOs.AdminHumanDetailData?> GetAdminHumanDetailAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetAdminHumanDetailAsync(userId, ct);
    }

    public async Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetEmailCooldownInfoAsync(pendingEmailId, ct);
    }

    public Task<IReadOnlyList<UserSearchResult>> SearchApprovedUsersAsync(
        string query, CancellationToken ct = default)
    {
        return Task.FromResult(
            ProfilesProfileService.SearchApprovedUsersFromSnapshot(_byUserId.Values, query));
    }

    public Task<IReadOnlyList<HumanSearchResult>> SearchHumansAsync(
        string query, CancellationToken ct = default)
    {
        return Task.FromResult(
            ProfilesProfileService.SearchHumansFromSnapshot(_byUserId.Values, query));
    }

    public async Task<IReadOnlyList<ProfileLanguage>> GetProfileLanguagesAsync(
        Guid profileId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetProfileLanguagesAsync(profileId, ct);
    }

    public async Task SaveProfileLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        await inner.SaveProfileLanguagesAsync(profileId, languages, ct);

        // SaveProfileLanguagesAsync takes profileId, not userId; resolve via the dict.
        // At ~500-user scale an O(n) scan over dict values is negligible.
        // _byUserId.Values is a ConcurrentDictionary snapshot — safe against concurrent
        // adds/removes. O(n) at ≤500 users.
        var userId = _byUserId.Values.FirstOrDefault(p => p.ProfileId == profileId)?.UserId;
        if (userId.HasValue)
            await RefreshEntryAsync(userId.Value, ct);
    }

    // ==========================================================================
    // IFullProfileInvalidator implementation
    // ==========================================================================

    /// <inheritdoc cref="IFullProfileInvalidator.InvalidateAsync"/>
    public Task InvalidateAsync(Guid userId, CancellationToken ct = default) =>
        RefreshEntryAsync(userId, ct);

    // ==========================================================================
    // Writes — delegate then invalidate
    // ==========================================================================

    public async Task SetMembershipTierAsync(
        Guid userId, MembershipTier tier, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var navBadge = scope.ServiceProvider.GetRequiredService<INavBadgeCacheInvalidator>();
        await inner.SetMembershipTierAsync(userId, tier, ct);
        navBadge.Invalidate();
        await RefreshEntryAsync(userId, ct);
    }

    public async Task SetProfilePictureAsync(
        Guid userId, byte[] pictureData, string contentType, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        await inner.SetProfilePictureAsync(userId, pictureData, contentType, ct);
        await RefreshEntryAsync(userId, ct);
    }

    public async Task<Guid> SaveProfileAsync(
        Guid userId, string displayName, ProfileSaveRequest request, string language,
        CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var navBadge = scope.ServiceProvider.GetRequiredService<INavBadgeCacheInvalidator>();
        var notificationMeter = scope.ServiceProvider.GetRequiredService<INotificationMeterCacheInvalidator>();
        var result = await inner.SaveProfileAsync(userId, displayName, request, language, ct);

        navBadge.Invalidate();
        notificationMeter.Invalidate();
        await RefreshEntryAsync(userId, ct);

        return result;
    }

    // Shift-authorization cache invalidation lives on
    // AccountDeletionService.RequestDeletionAsync (peterdrier/Humans#314 review)
    // — keeps eviction co-located with the orchestrating mutation, so direct
    // callers of IAccountDeletionService don't need this decorator for
    // correctness. The decorator's job here is just the FullProfile refresh.
    public async Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var result = await inner.RequestDeletionAsync(userId, ct);
        if (result.Success)
            await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var result = await inner.CancelDeletionAsync(userId, ct);
        if (result.Success)
            await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task SaveCVEntriesAsync(Guid userId, IReadOnlyList<CVEntry> entries, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        await inner.SaveCVEntriesAsync(userId, entries, ct);
        await RefreshEntryAsync(userId, ct);
    }

    // ==========================================================================
    // Onboarding-section writes — delegate to inner, then invalidate cross-cutting
    // caches (nav badge, notification meter) and refresh the FullProfile entry.
    // ==========================================================================

    public async Task<IReadOnlyList<Profile>> GetReviewableProfilesAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetReviewableProfilesAsync(ct);
    }

    public async Task<int> GetPendingReviewCountAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetPendingReviewCountAsync(ct);
    }

    public async Task<OnboardingResult> ClearConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var navBadge = scope.ServiceProvider.GetRequiredService<INavBadgeCacheInvalidator>();
        var notificationMeter = scope.ServiceProvider.GetRequiredService<INotificationMeterCacheInvalidator>();

        var result = await inner.ClearConsentCheckAsync(userId, reviewerId, notes, ct);
        if (result.Success)
        {
            navBadge.Invalidate();
            notificationMeter.Invalidate();
            await RefreshEntryAsync(userId, ct);
        }
        return result;
    }

    public async Task<OnboardingResult> FlagConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var navBadge = scope.ServiceProvider.GetRequiredService<INavBadgeCacheInvalidator>();
        var notificationMeter = scope.ServiceProvider.GetRequiredService<INotificationMeterCacheInvalidator>();

        var result = await inner.FlagConsentCheckAsync(userId, reviewerId, notes, ct);
        if (result.Success)
        {
            navBadge.Invalidate();
            notificationMeter.Invalidate();
            await RefreshEntryAsync(userId, ct);
        }
        return result;
    }

    public async Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var navBadge = scope.ServiceProvider.GetRequiredService<INavBadgeCacheInvalidator>();
        var notificationMeter = scope.ServiceProvider.GetRequiredService<INotificationMeterCacheInvalidator>();

        var result = await inner.RejectSignupAsync(userId, reviewerId, reason, ct);
        if (result.Success)
        {
            navBadge.Invalidate();
            notificationMeter.Invalidate();
            await RefreshEntryAsync(userId, ct);
        }
        return result;
    }

    public async Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var navBadge = scope.ServiceProvider.GetRequiredService<INavBadgeCacheInvalidator>();
        var notificationMeter = scope.ServiceProvider.GetRequiredService<INotificationMeterCacheInvalidator>();

        var result = await inner.ApproveVolunteerAsync(userId, adminId, ct);
        if (result.Success)
        {
            navBadge.Invalidate();
            notificationMeter.Invalidate();
            await RefreshEntryAsync(userId, ct);
        }
        return result;
    }

    public async Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);

        var result = await inner.SuspendAsync(userId, adminId, notes, ct);
        if (result.Success)
            await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);

        var result = await inner.UnsuspendAsync(userId, adminId, ct);
        if (result.Success)
            await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task<bool> SetConsentCheckPendingAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var navBadge = scope.ServiceProvider.GetRequiredService<INavBadgeCacheInvalidator>();
        var notificationMeter = scope.ServiceProvider.GetRequiredService<INotificationMeterCacheInvalidator>();

        var set = await inner.SetConsentCheckPendingAsync(userId, ct);
        if (set)
        {
            navBadge.Invalidate();
            notificationMeter.Invalidate();
            await RefreshEntryAsync(userId, ct);
        }
        return set;
    }

    public async Task<bool> AnonymizeExpiredProfileAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var anonymized = await inner.AnonymizeExpiredProfileAsync(userId, ct);
        if (anonymized)
        {
            // The profile fields used by the FullProfile projection changed —
            // refresh the cache entry so downstream readers see the anonymized
            // view immediately.
            await RefreshEntryAsync(userId, ct);
        }
        return anonymized;
    }

    public async Task<IReadOnlySet<Guid>> SuspendForMissingConsentAsync(
        IReadOnlyCollection<Guid> userIds,
        Instant now,
        CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var suspendedIds = await inner.SuspendForMissingConsentAsync(userIds, now, ct);
        // IsSuspended is part of the FullProfile projection — refresh the
        // cache entry for every user that was actually mutated so downstream
        // readers see the suspended view immediately.
        foreach (var userId in suspendedIds)
        {
            await RefreshEntryAsync(userId, ct);
        }
        return suspendedIds;
    }

    public async Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>
        DowngradeTierForExpiredAsync(
            MembershipTier currentTier,
            IReadOnlyCollection<Guid> userIdsToKeep,
            IReadOnlyDictionary<Guid, MembershipTier> fallbackTierByUser,
            Instant now,
            CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var downgrades = await inner.DowngradeTierForExpiredAsync(
            currentTier, userIdsToKeep, fallbackTierByUser, now, ct);
        // MembershipTier is part of the FullProfile projection — refresh the
        // cache entry for every downgraded user.
        foreach (var (userId, _) in downgrades)
        {
            await RefreshEntryAsync(userId, ct);
        }
        return downgrades;
    }
}
