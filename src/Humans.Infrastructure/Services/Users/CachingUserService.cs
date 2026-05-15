using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Services.Users;

/// <summary>
/// Issue #703. Singleton caching decorator for <see cref="IUserService"/>.
/// Inherits <see cref="TrackedCache{TKey, TValue}"/> for a hit/miss-tracked cache of
/// <see cref="UserInfo"/> entries keyed by userId — the canonical
/// "everything-about-a-person" cache spanning the User and Profile sections
/// (8 contributing tables).
/// </summary>
/// <remarks>
/// <para>
/// Pattern mirrors <c>CachingProfileService</c>: dict hits served
/// synchronously, cache miss refills via the inner Scoped
/// <see cref="IUserService"/>, every write through this surface delegates and
/// then refreshes the affected entry. Identity-machinery write paths
/// (<c>UserManager.UpdateAsync</c>, sign-in <c>LastLoginAt</c> bumps, OAuth
/// callback <c>UserEmail</c> writes) are caught by
/// <c>UserInfoSaveChangesInterceptor</c> in Infrastructure, which invokes
/// <see cref="IUserInfoInvalidator.InvalidateAsync"/> for every userId
/// touched by the affected entity set.
/// </para>
/// <para>
/// Registered as Singleton so the dict persists across requests. Scoped
/// dependencies (the inner <see cref="IUserService"/>) are resolved per-call
/// via <see cref="IServiceScopeFactory"/> to avoid the captured-scoped
/// anti-pattern. <see cref="IUserRepository"/>, <see cref="IUserEmailRepository"/>,
/// <see cref="IProfileRepository"/>, and <see cref="IContactFieldRepository"/>
/// are injected directly because they are also Singleton
/// (<c>IDbContextFactory</c>-based).
/// </para>
/// </remarks>
public sealed class CachingUserService : TrackedCache<Guid, UserInfo>, IUserService, IUserMerge, IUserInfoInvalidator
{
    /// <summary>
    /// DI service key under which the undecorated (inner) <see cref="IUserService"/>
    /// is registered. Used by the Singleton decorator to resolve the Scoped inner
    /// service per-call without triggering self-resolution on the unkeyed
    /// <see cref="IUserService"/> registration.
    /// </summary>
    public const string InnerServiceKey = "user-inner";

    private readonly IUserRepository _userRepository;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly IContactFieldRepository _contactFieldRepository;
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CachingUserService> _logger;

    public CachingUserService(
        IUserRepository userRepository,
        IUserEmailRepository userEmailRepository,
        IProfileRepository profileRepository,
        IContactFieldRepository contactFieldRepository,
        ICommunicationPreferenceRepository communicationPreferenceRepository,
        IServiceScopeFactory scopeFactory,
        ILogger<CachingUserService> logger)
        : base("User.UserInfo")
    {
        _userRepository = userRepository;
        _userEmailRepository = userEmailRepository;
        _profileRepository = profileRepository;
        _contactFieldRepository = contactFieldRepository;
        _communicationPreferenceRepository = communicationPreferenceRepository;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ==========================================================================
    // UserInfo reads
    // ==========================================================================

    public ValueTask<UserInfo?> GetUserInfoAsync(Guid userId, CancellationToken ct = default)
    {
        if (TryGet(userId, out var hit))
            return new ValueTask<UserInfo?>(hit);

        return new ValueTask<UserInfo?>(LoadAndCacheAsync(userId, ct));
    }

    private async Task<UserInfo?> LoadAndCacheAsync(Guid userId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserService>(InnerServiceKey);
        var result = await inner.GetUserInfoAsync(userId, ct);
        if (result is not null)
            Set(userId, result);
        return result;
    }

    /// <inheritdoc cref="IUserService.GetAllUserInfos" />
    public IReadOnlyCollection<UserInfo> GetAllUserInfos() => Values.ToArray();

    /// <summary>
    /// Rebuilds the cache entry for <paramref name="userId"/> directly from
    /// repositories. If the user no longer exists, the entry is removed.
    /// </summary>
    private async Task RefreshEntryAsync(Guid userId, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user is null)
        {
            Invalidate(userId);
            return;
        }

        var userEmails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);
        var participations = await _userRepository.GetEventParticipationsByUserIdAsync(userId, ct);
        var loginsMap = await _userRepository.GetExternalLoginsByUserIdsAsync(new[] { userId }, ct);
        var externalLogins = loginsMap.TryGetValue(userId, out var logins)
            ? logins
            : Array.Empty<(string Provider, string ProviderKey)>();

        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);
        IReadOnlyList<ContactField> contactFields = Array.Empty<ContactField>();
        IReadOnlyList<ProfileLanguage> languages = Array.Empty<ProfileLanguage>();
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory = Array.Empty<VolunteerHistoryEntry>();
        if (profile is not null)
        {
            contactFields = await _contactFieldRepository.GetByProfileIdReadOnlyAsync(profile.Id, ct);
            languages = profile.Languages.ToList();
            volunteerHistory = profile.VolunteerHistory.ToList();
        }

        var communicationPreferences = await _communicationPreferenceRepository
            .GetByUserIdReadOnlyAsync(userId, ct);

        Set(userId, UserInfo.Create(
            user, userEmails, participations, externalLogins,
            profile, contactFields, languages, volunteerHistory,
            communicationPreferences));
    }

    /// <summary>
    /// Populates the inherited cache with a <see cref="UserInfo"/> for every
    /// existing user at startup. Bulk-loads each of the 8 contributing tables
    /// once and indexes by userId so per-user materialization is allocation-only.
    /// Trivial at ~500-user scale.
    /// </summary>
    public async Task WarmAllAsync(CancellationToken ct = default)
    {
        var users = await _userRepository.GetAllAsync(ct);
        if (users.Count == 0) return;

        var userIds = users.Select(u => u.Id).ToList();

        var allEmails = await _userEmailRepository.GetAllAsync(ct);
        var emailsByUser = allEmails
            .GroupBy(e => e.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<UserEmail>)g.ToList());

        var loginsByUser = await _userRepository.GetExternalLoginsByUserIdsAsync(userIds, ct);

        var profiles = await _profileRepository.GetAllAsync(ct);
        var profileByUser = profiles.ToDictionary(p => p.UserId);

        var allContactFields = await _contactFieldRepository.GetAllAsync(ct);
        var contactFieldsByProfile = allContactFields
            .GroupBy(c => c.ProfileId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ContactField>)g.ToList());

        var participationsByUser = await _userRepository
            .GetEventParticipationsByUserIdsAsync(userIds, ct);

        var allPreferences = await _communicationPreferenceRepository.GetAllAsync(ct);
        var preferencesByUser = allPreferences
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CommunicationPreference>)g.ToList());

        foreach (var user in users)
        {
            var emails = emailsByUser.TryGetValue(user.Id, out var es)
                ? es : Array.Empty<UserEmail>();
            var logins = loginsByUser.TryGetValue(user.Id, out var ls)
                ? ls : Array.Empty<(string Provider, string ProviderKey)>();
            var participations = participationsByUser.TryGetValue(user.Id, out var ps)
                ? ps : (IReadOnlyList<EventParticipation>)Array.Empty<EventParticipation>();

            profileByUser.TryGetValue(user.Id, out var profile);
            IReadOnlyList<ContactField> contactFields = Array.Empty<ContactField>();
            IReadOnlyList<ProfileLanguage> languages = Array.Empty<ProfileLanguage>();
            IReadOnlyList<VolunteerHistoryEntry> volunteerHistory = Array.Empty<VolunteerHistoryEntry>();
            if (profile is not null)
            {
                contactFields = contactFieldsByProfile.TryGetValue(profile.Id, out var cf)
                    ? cf : Array.Empty<ContactField>();
                languages = profile.Languages.ToList();
                volunteerHistory = profile.VolunteerHistory.ToList();
            }

            var preferences = preferencesByUser.TryGetValue(user.Id, out var pp)
                ? pp : Array.Empty<CommunicationPreference>();

            Set(user.Id, UserInfo.Create(
                user, emails, participations, logins,
                profile, contactFields, languages, volunteerHistory,
                preferences));
        }
    }

    // ==========================================================================
    // IUserInfoInvalidator
    // ==========================================================================

    /// <inheritdoc cref="IUserInfoInvalidator.InvalidateAsync" />
    public Task InvalidateAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        _logger.LogDebug(
            "UserInfo invalidate userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, System.IO.Path.GetFileName(filePath));

        return RefreshEntryAsync(userId, ct);
    }

    // ==========================================================================
    // Inner delegation — every other IUserService method passes through and
    // refreshes the affected entry on writes.
    // ==========================================================================

    private async Task<T> WithInnerAsync<T>(Func<IUserService, Task<T>> work)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserService>(InnerServiceKey);
        return await work(inner);
    }

    private async Task WithInnerAsync(Func<IUserService, Task> work)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserService>(InnerServiceKey);
        await work(inner);
    }

    // Reads — pure pass-through; the dict is keyed by userId, these methods
    // either don't fit that key or are infrequent enough not to warrant caching.

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetByIdAsync(userId, ct));

    public Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetByIdsAsync(userIds, ct));

    public Task<IReadOnlyDictionary<Guid, User>> GetByIdsWithEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetByIdsWithEmailsAsync(userIds, ct));

    public Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default)
    {
        var snapshot = Values;
        var result = new List<EventParticipation>();
        foreach (var u in snapshot)
        {
            foreach (var p in u.EventParticipations)
            {
                if (p.Year != year) continue;
                result.Add(new EventParticipation
                {
                    Id = p.Id,
                    UserId = u.Id,
                    Year = p.Year,
                    Status = p.Status,
                    Source = p.Source,
                    DeclaredAt = p.DeclaredAt
                });
            }
        }
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetAllUsersAsync(ct));

    public Task<User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetByEmailOrAlternateAsync(email, ct));

#pragma warning disable CS0618
    public Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(
        string email, Guid excludeUserId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetOtherUserIdHavingGoogleEmailAsync(email, excludeUserId, ct));
#pragma warning restore CS0618

    public Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetAccountsDueForAnonymizationAsync(now, ct));

    public Task<IReadOnlySet<Guid>> GetMergedSourceIdsAsync(
        Guid targetUserId, CancellationToken ct = default)
    {
        var ids = new HashSet<Guid>();
        foreach (var u in Values)
        {
            if (u.MergedToUserId == targetUserId)
                ids.Add(u.Id);
        }
        return Task.FromResult<IReadOnlySet<Guid>>(ids);
    }

    public Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetUsersWithLoginsButNoEmailsAsync(ct));

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetExternalLoginsByUserIdsAsync(userIds, ct));

    // Writes — delegate to inner, then refresh the affected entry.

    public async Task<bool> TrySetGoogleEmailStatusFromSyncAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.TrySetGoogleEmailStatusFromSyncAsync(userId, status, ct));
        if (result) await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.UpdateDisplayNameAsync(userId, displayName, ct));
        await RefreshEntryAsync(userId, ct);
    }

    public async Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default)
    {
        var updated = await WithInnerAsync(inner =>
            inner.SetDeletionPendingAsync(userId, requestedAt, scheduledFor, eligibleAfter, ct));
        if (updated) await RefreshEntryAsync(userId, ct);
        return updated;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var updated = await WithInnerAsync(inner => inner.ClearDeletionAsync(userId, ct));
        if (updated) await RefreshEntryAsync(userId, ct);
        return updated;
    }

    public Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default) =>
        WithInnerAsync(async inner =>
        {
            await inner.SetLastConsentReminderSentAsync(userId, sentAt, ct);
            await RefreshEntryAsync(userId, ct);
        });

    public async Task<EventParticipation> DeclareNotAttendingAsync(
        Guid userId, int year, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.DeclareNotAttendingAsync(userId, year, ct));
        await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.UndoNotAttendingAsync(userId, year, ct));
        if (result) await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task SetParticipationFromTicketSyncAsync(
        Guid userId, int year, ParticipationStatus status, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.SetParticipationFromTicketSyncAsync(userId, year, status, ct));
        await RefreshEntryAsync(userId, ct);
    }

    public async Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.RemoveTicketSyncParticipationAsync(userId, year, ct));
        await RefreshEntryAsync(userId, ct);
    }

    public async Task<int> BackfillParticipationsAsync(
        int year,
        List<(Guid UserId, ParticipationStatus Status)> entries,
        CancellationToken ct = default)
    {
        var count = await WithInnerAsync(inner => inner.BackfillParticipationsAsync(year, entries, ct));
        foreach (var (userId, _) in entries) await RefreshEntryAsync(userId, ct);
        return count;
    }

    public async Task<string?> PurgeOwnDataAsync(Guid userId, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.PurgeOwnDataAsync(userId, ct));
        // Inner already invoked IFullProfileInvalidator on success; we refresh
        // our own dict whether or not the row existed (RefreshEntryAsync removes
        // the entry when the user is gone).
        await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.ApplyExpiredDeletionAnonymizationAsync(userId, ct));
        if (result is not null) await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.AnonymizeForMergeAsync(sourceUserId, targetUserId, now, ct));
        if (result)
        {
            await RefreshEntryAsync(sourceUserId, ct);
            await RefreshEntryAsync(targetUserId, ct);
        }
        return result;
    }

    public async Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var deleted = await WithInnerAsync(inner => inner.DeleteUsersAsync(userIds, ct));
        foreach (var userId in userIds)
        {
            Invalidate(userId);
        }
        return deleted;
    }

    public async Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var deleted = await WithInnerAsync(inner => inner.DeleteAllExternalLoginsForUserAsync(userId, ct));
        if (deleted > 0) await RefreshEntryAsync(userId, ct);
        return deleted;
    }

    // ==========================================================================
    // IUserMerge — delegate to inner, then refresh both ends.
    // ==========================================================================

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        await WithInnerAsync(inner =>
            inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));
        await RefreshEntryAsync(mergedFromUserId, ct);
        await RefreshEntryAsync(mergedToUserId, ct);
    }

}
