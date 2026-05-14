using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using ProfilesProfileService = Humans.Application.Services.Profile.ProfileService;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Services.Profiles;

namespace Humans.Infrastructure.Services.Profiles;

/// <summary>
/// Singleton caching decorator for <see cref="IProfileService"/>. Inherits
/// <see cref="TrackedCache{TKey, TValue}"/> to own a hit/miss-tracked cache of
/// <see cref="FullProfile"/> entries keyed by userId. Reads serve cache hits
/// synchronously via <see cref="ValueTask{TResult}"/>; writes reload the
/// affected entry from repositories via <c>RefreshEntryAsync</c>.
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
public sealed class CachingProfileService : TrackedCache<Guid, FullProfile>, IProfileService, IFullProfileInvalidator
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
    private readonly IContactFieldRepository _contactFieldRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CachingProfileService> _logger;

    // Issue #703: every Profile write also flushes the UserInfo cache. Resolved
    // lazily via IServiceProvider rather than ctor-injected because the
    // invalidator (CachingUserService) is registered in the Users section and
    // we don't want a section-extension ordering dependency at DI build time —
    // and "extra flush > missed flush" was the explicit call. Resolve-once on
    // first use; this is a Singleton so the result is stable for the process.
    private readonly IServiceProvider _services;
    private IUserInfoInvalidator? _userInfoInvalidator;

    public CachingProfileService(
        IProfileRepository profileRepository,
        IUserEmailRepository userEmailRepository,
        IContactFieldRepository contactFieldRepository,
        IServiceScopeFactory scopeFactory,
        IServiceProvider services,
        ILogger<CachingProfileService> logger)
        : base("Profile.FullProfile")
    {
        _profileRepository = profileRepository;
        _userEmailRepository = userEmailRepository;
        _contactFieldRepository = contactFieldRepository;
        _scopeFactory = scopeFactory;
        _services = services;
        _logger = logger;
    }

    private async Task InvalidateUserInfoAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            var invalidator = _userInfoInvalidator
                ??= _services.GetService<IUserInfoInvalidator>();
            if (invalidator is not null)
                await invalidator.InvalidateAsync(userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "CachingProfileService: UserInfo invalidation failed for {UserId}: {ExType}",
                userId, ex.GetType().Name);
        }
    }

    // ==========================================================================
    // Reads — dict cache + pass-through
    // ==========================================================================

    // Pure pass-through: the inherited FullProfile cache is the Profile cache now.
    // No separate IMemoryCache layer for raw Profile entities.
    public async Task<Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetProfileAsync(userId, ct);
    }

    public ValueTask<FullProfile?> GetFullProfileAsync(Guid userId, CancellationToken ct = default)
    {
        if (TryGet(userId, out var hit))
            return new ValueTask<FullProfile?>(hit);

        return new ValueTask<FullProfile?>(LoadAndCacheAsync(userId, ct));
    }

    private async Task<FullProfile?> LoadAndCacheAsync(Guid userId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var result = await inner.GetFullProfileAsync(userId, ct);
        if (result is not null)
            Set(userId, result);
        return result;
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    /// <summary>
    /// Reloads the <see cref="FullProfile"/> for <paramref name="userId"/> directly
    /// from repositories and upserts it in the inherited cache.
    /// If the profile or user no longer exists, the entry is removed instead.
    /// Called after every mutation so the dict stays consistent without eviction.
    /// </summary>
    private async Task RefreshEntryAsync(Guid userId, CancellationToken ct)
    {
        // Issue #703: every FullProfile refresh also invalidates UserInfo.
        // Profile and its child tables (ContactField, ProfileLanguage,
        // VolunteerHistoryEntry) feed into UserInfo too, so a mutation that
        // refreshes FullProfile must drop the UserInfo entry to avoid serving
        // stale joined data from the read-model cache. Done first so that
        // an exception in the FullProfile path below doesn't leak a stale
        // UserInfo entry.
        await InvalidateUserInfoAsync(userId, ct);

        // If any repository call throws, the dict retains the pre-mutation entry;
        // the next cache miss will re-load from the inner service. This is tolerable
        // at single-server ~500-user scale — the surface area for a divergence
        // window is a single process' lifetime.
        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);
        if (profile is null)
        {
            Invalidate(userId);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        var user = await userService.GetByIdAsync(userId, ct);
        if (user is null)
        {
            Invalidate(userId);
            return;
        }

        var userEmails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);

        await PopulateStateIfNullAsync(profile, ct);

        // Issue #635 (§15i): pass userEmails directly so PrimaryEmail /
        // AllVerifiedEmails / GoogleEmail derive from already-loaded data
        // without per-property repo calls.
        Set(userId, FullProfile.Create(profile, user, profile.VolunteerHistory.ToList(), userEmails));
    }

    /// <summary>
    /// Issue #635 (§15i): if the loaded <paramref name="profile"/>'s State is
    /// null, lazy-compute the canonical value and write it back via
    /// <see cref="IProfileRepository.WriteBackStateIfNullAsync"/>. The repo
    /// guard (State IS NULL) keeps this idempotent under concurrent reads.
    /// Mutates the in-memory profile so the cached <see cref="FullProfile"/>
    /// reflects the computed value even when the write-back is a no-op.
    /// Best-effort: a write failure logs and proceeds — the next read retries.
    /// Used by both <see cref="RefreshEntryAsync"/> (single-user path) and
    /// <see cref="WarmAllAsync"/> (startup warmup) so legacy NULL rows are
    /// populated regardless of how the FullProfile entry is built.
    /// </summary>
    private async Task PopulateStateIfNullAsync(Profile profile, CancellationToken ct)
    {
        if (profile.State is not null) return;

        var computed = ComputeProfileState(profile);
        try
        {
            await _profileRepository.WriteBackStateIfNullAsync(profile.UserId, computed, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lazy backfill is best-effort; the next read retries. Don't
            // block FullProfile resolution on a write failure. LogWarning
            // (no exception object) per memory/code/always-log-problems.md
            // for expected/transient failures.
            _logger.LogWarning(
                "CachingProfileService lazy ProfileState write-back failed for {UserId}: {ExType}",
                profile.UserId, ex.GetType().Name);
        }
        profile.State = computed;
    }

    /// <summary>
    /// Issue #635 (§15i): computes the ProfileState that should be persisted
    /// for a row whose stored value is NULL, from <c>IsSuspended</c> +
    /// required-field presence (via <see cref="Profile.HasRequiredIdentityFields"/>).
    /// Suspended dominates; otherwise rows with all required identity fields
    /// populated are <see cref="ProfileState.Active"/>, others are
    /// <see cref="ProfileState.Stub"/>. The shared predicate keeps this lazy-
    /// compute path in lockstep with <c>ProfileService.SaveProfileAsync</c> /
    /// <c>SetSuspendedAsync</c>.
    /// </summary>
    internal static ProfileState ComputeProfileState(Profile profile)
    {
#pragma warning disable HUM_PROFILE_ISSUSPENDED
        if (profile.IsSuspended)
            return ProfileState.Suspended;
#pragma warning restore HUM_PROFILE_ISSUSPENDED

        return profile.HasRequiredIdentityFields() ? ProfileState.Active : ProfileState.Stub;
    }

    /// <summary>
    /// Populates the inherited cache with a <see cref="FullProfile"/> for every
    /// existing profile. Called at application startup by
    /// <c>FullProfileWarmupHostedService</c> so bulk-read methods
    /// (<see cref="GetBirthdayProfilesAsync"/>,
    /// <see cref="GetApprovedProfilesWithLocationAsync"/>,
    /// <see cref="SearchProfilesAsync"/>) return complete results immediately
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

        // Issue #635 (§15i): bulk-load all UserEmails once, group by userId,
        // and feed FullProfile.Create so PrimaryEmail / AllVerifiedEmails /
        // GoogleEmail are populated without per-user repo calls.
        var allUserEmails = await _userEmailRepository.GetAllAsync(ct);
        var emailsByUserId = allUserEmails
            .GroupBy(e => e.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<UserEmail>)g.ToList());

        foreach (var profile in profiles)
        {
            if (!users.TryGetValue(profile.UserId, out var user))
                continue;

            await PopulateStateIfNullAsync(profile, ct);

            var userEmails = emailsByUserId.TryGetValue(profile.UserId, out var list)
                ? list
                : Array.Empty<UserEmail>();
            Set(profile.UserId,
                FullProfile.Create(profile, user, profile.VolunteerHistory.ToList(), userEmails));
        }
    }

    public async Task<IReadOnlyDictionary<Guid, Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetByUserIdsAsync(userIds, ct);
    }

    public async Task<(byte[] Data, string ContentType)?> GetProfilePictureAsync(
        Guid profileId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetProfilePictureAsync(profileId, ct);
    }

    public async Task<ProfilePictureMigrationSnapshot> GetProfilePictureMigrationSnapshotAsync(
        CancellationToken ct = default)
    {
        // Diagnostic snapshot for the DB→FS migration verification page
        // (issue nobodies-collective/Humans#702). Not cached — admin operator
        // tool, runs on demand, and the freshest possible read is what's
        // useful immediately after a Run.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetProfilePictureMigrationSnapshotAsync(ct);
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
            ProfilesProfileService.GetBirthdayProfilesFromSnapshot(Values, month));
    }

    public Task<IReadOnlyList<Application.DTOs.LocationProfileInfo>>
        GetApprovedProfilesWithLocationAsync(CancellationToken ct = default)
    {
        return Task.FromResult(
            ProfilesProfileService.GetApprovedProfilesWithLocationFromSnapshot(Values));
    }

    public async Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        return await inner.GetEmailCooldownInfoAsync(pendingEmailId, ct);
    }

    public async Task<IReadOnlyList<HumanSearchResult>> SearchProfilesAsync(
        string query,
        PersonSearchFields fields,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (fields == PersonSearchFields.None || string.IsNullOrWhiteSpace(query) || limit <= 0)
            return Array.Empty<HumanSearchResult>();

        // Snapshot the dict values once so the search reads a stable
        // collection across awaits.
        var snapshot = Values.Where(p => !p.IsRejected).ToList();

        IReadOnlyDictionary<Guid, IReadOnlyList<ContactField>>? contactFieldsByProfileId = null;
        if ((fields & (PersonSearchFields.Bio | PersonSearchFields.Admin)) != PersonSearchFields.None)
        {
            var all = await _contactFieldRepository.GetAllAsync(ct);
            contactFieldsByProfileId = all
                .GroupBy(cf => cf.ProfileId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<ContactField>)g.ToList());
        }

        return PersonSearchMatcher.Match(snapshot, query, fields, contactFieldsByProfileId, limit);
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

        // SaveProfileLanguagesAsync takes profileId, not userId; resolve via the cache.
        // At ~500-user scale an O(n) scan over Values is negligible. Values is a
        // ConcurrentDictionary snapshot — safe against concurrent adds/removes.
        var userId = Values.FirstOrDefault(p => p.ProfileId == profileId)?.UserId;
        if (userId.HasValue)
            await RefreshEntryAsync(userId.Value, ct);
    }

    // ==========================================================================
    // IUserMerge implementation — delegate to inner, then evict both users from
    // the FullProfile dict. Eviction (not RefreshEntryAsync) is intentional:
    // ReassignAsync runs inside the orchestrator's TransactionScope, so a
    // DB-backed rebuild here would read uncommitted state. Eviction is
    // rollback-safe — the next read repopulates from whatever the DB ends up
    // with after the scope completes (or rolls back).
    // ==========================================================================

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        await inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct);

        Invalidate(mergedFromUserId);
        Invalidate(mergedToUserId);

        // Issue #703: also flush UserInfo for both ends. CachingUserService's
        // own ReassignAsync covers the user-section invalidation; this is the
        // profile-section side of the merge.
        await InvalidateUserInfoAsync(mergedFromUserId, ct);
        await InvalidateUserInfoAsync(mergedToUserId, ct);
    }

    // ==========================================================================
    // IFullProfileInvalidator implementation
    // ==========================================================================

    /// <inheritdoc cref="IFullProfileInvalidator.InvalidateAsync"/>
    public Task InvalidateAsync(
        Guid userId,
        CancellationToken ct = default,
        [System.Runtime.CompilerServices.CallerMemberName] string memberName = "",
        [System.Runtime.CompilerServices.CallerFilePath] string filePath = "")
    {
        // Issue #635 (§15i): caller log via [CallerMemberName] / [CallerFilePath]
        // params — compiler fills these at the callsite so direct callers and
        // test mocks don't have to. LogDebug is gated by the logger config
        // (Debug enabled in dev, off in prod); the log is the canonical way to
        // verify every Profile-affecting write hits the invalidator during
        // exploratory testing on the preview environment.
        _logger.LogDebug(
            "FullProfile invalidate userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, System.IO.Path.GetFileName(filePath));

        return RefreshEntryAsync(userId, ct);
    }

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

    public async Task EnsureStubProfileAsync(Guid userId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        await inner.EnsureStubProfileAsync(userId, ct);
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

    public async Task<OnboardingResult> RecordConsentCheckAsync(
        Guid userId, Guid reviewerId, ConsentCheckStatus result, string? notes,
        CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var navBadge = scope.ServiceProvider.GetRequiredService<INavBadgeCacheInvalidator>();
        var notificationMeter = scope.ServiceProvider.GetRequiredService<INotificationMeterCacheInvalidator>();

        var outcome = await inner.RecordConsentCheckAsync(userId, reviewerId, result, notes, ct);
        if (outcome.Success)
        {
            navBadge.Invalidate();
            notificationMeter.Invalidate();
            await RefreshEntryAsync(userId, ct);
        }
        return outcome;
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

    public async Task<OnboardingResult> SetSuspendedAsync(
        Guid userId, Guid adminId, bool suspended, string? notes,
        CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);

        var result = await inner.SetSuspendedAsync(userId, adminId, suspended, notes, ct);
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

    public async Task<bool> SetIbanAsync(Guid userId, string? iban, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IProfileService>(InnerServiceKey);
        var result = await inner.SetIbanAsync(userId, iban, ct);
        if (result)
            await RefreshEntryAsync(userId, ct);
        return result;
    }
}
