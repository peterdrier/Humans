using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Users;

public sealed class UserService(
    IUserRepository repo,
    IUserEmailRepository userEmailRepo,
    IProfileRepository profileRepo,
    IContactFieldRepository contactFieldRepo,
    ICommunicationPreferenceRepository communicationPreferenceRepo,
    IUserInfoInvalidator userInfoInvalidator,
    IAdminAuthorizationService adminAuthorization,
    IClock clock,
    ILogger<UserService> logger) : IUserService, IUserDataContributor, IUserMerge
{
    // --- User reads ---

    public async ValueTask<UserInfo?> GetUserInfoAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await repo.GetByIdAsync(userId, ct);
        if (user is null) return null;

        var userEmails = await userEmailRepo.GetByUserIdReadOnlyAsync(userId, ct);
        var participations = await repo.GetEventParticipationsByUserIdAsync(userId, ct);
        var externalLoginsMap = await repo.GetExternalLoginsByUserIdsAsync([userId], ct);
        var externalLogins = externalLoginsMap.TryGetValue(userId, out var logins)
            ? logins
            : [];

        var profile = await profileRepo.GetByUserIdReadOnlyAsync(userId, ct);
        IReadOnlyList<ContactField> contactFields = [];
        IReadOnlyList<ProfileLanguage> languages = [];
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory = [];
        if (profile is not null)
        {
            contactFields = await contactFieldRepo.GetByProfileIdReadOnlyAsync(profile.Id, ct);
            languages = profile.Languages.ToList();
            volunteerHistory = profile.VolunteerHistory.ToList();
        }

        var communicationPreferences = await communicationPreferenceRepo
            .GetByUserIdReadOnlyAsync(userId, ct);

        return UserInfo.Create(
            user, userEmails, participations, externalLogins,
            profile, contactFields, languages, volunteerHistory,
            communicationPreferences);
    }

    public Task<IReadOnlyCollection<UserInfo>> GetAllUserInfosAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(
            "GetAllUserInfosAsync is only meaningful through CachingUserService. " +
            "If this is being called on the inner UserService it indicates a DI " +
            "registration mistake — IUserService should resolve to CachingUserService.");

    public ValueTask<IReadOnlyDictionary<Guid, UserInfo>> GetUserInfosAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "GetUserInfosAsync is only meaningful through CachingUserService — " +
            "the decorator serves cached hits from the dict and refills misses " +
            "through inner.GetUserInfoAsync. If this is being called on the inner " +
            "UserService it indicates a DI registration mistake.");

    public Task<IReadOnlyList<HumanSearchResult>> SearchUsersAsync(
        string query, PersonSearchFields fields, int limit = 10, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "SearchUsersAsync runs against the cached UserInfo snapshot in " +
            "CachingUserService. If this is being called on the inner UserService " +
            "it indicates a DI registration mistake — IUserService should resolve " +
            "to CachingUserService.");

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default) =>
        repo.GetByIdAsync(userId, ct);

    public Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        repo.GetByIdsAsync(userIds, ct);

    public Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken ct = default) =>
        repo.GetAllAsync(ct);

    public async Task<string?> PurgeOwnDataAsync(Guid userId, CancellationToken ct = default)
    {
        var displayName = await repo.PurgeAsync(userId, ct);
        if (displayName is null)
            return null;

        // Cross-section invalidations belong to IAccountDeletionService.PurgeAsync.
        await userInfoInvalidator.InvalidateAsync(userId, ct);

        logger.LogWarning("Purged human {DisplayName} ({HumanId})", displayName, userId);

        return displayName;
    }

    public async Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Own-data only — cross-section cascade lives in IAccountDeletionService.
        var result = await repo.ApplyExpiredDeletionAnonymizationAsync(userId, ct);
        if (result is not null)
            await userInfoInvalidator.InvalidateAsync(userId, ct);
        return result;
    }

    public Task<User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default)
    {
        var normalized = EmailNormalization.NormalizeForComparison(email);
        var alternate = GetAlternateEmail(normalized);
        return repo.GetByEmailOrAlternateAsync(normalized, alternate, ct);
    }

    [Obsolete("Issue nobodies-collective/Humans#687: User.GoogleEmail is being deprecated. Use IUserEmailService.GetOtherUserIdHavingEmailAsync.")]
    public Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(
        string email, Guid excludeUserId, CancellationToken ct = default) =>
        repo.GetOtherUserIdHavingGoogleEmailAsync(email, excludeUserId, ct);

    public Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default) =>
        repo.GetAccountsDueForAnonymizationAsync(now, ct);

    // --- User writes ---

    public async Task<bool> TrySetGoogleEmailStatusFromSyncAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default)
    {
        if (status == GoogleEmailStatus.Valid)
        {
            var user = await repo.GetByIdAsync(userId, ct);
            if (user is null || user.GoogleEmailStatus == GoogleEmailStatus.Rejected)
                return false;
        }

        return await SetGoogleEmailStatusInternalAsync(userId, status, ct);
    }

    // Private write — Try variant is the only external entry point (sync-driven; Rejected is terminal).
    private async Task<bool> SetGoogleEmailStatusInternalAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default)
    {
        var set = await repo.SetGoogleEmailStatusAsync(userId, status, ct);
        if (set)
            await userInfoInvalidator.InvalidateAsync(userId, ct);
        return set;
    }

    public async Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default)
    {
        var updated = await repo.UpdateDisplayNameAsync(userId, displayName, ct);
        if (updated)
            await userInfoInvalidator.InvalidateAsync(userId, ct);
    }

    public async Task SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default)
    {
        var updated = await repo.SetPreferredLanguageAsync(userId, preferredLanguage, ct);
        if (updated)
            await userInfoInvalidator.InvalidateAsync(userId, ct);
    }

    public async Task SetICalTokenAsync(Guid userId, Guid token, CancellationToken ct = default)
    {
        var updated = await repo.SetICalTokenAsync(userId, token, ct);
        if (updated)
            await userInfoInvalidator.InvalidateAsync(userId, ct);
    }

    public async Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default)
    {
        var updated = await repo.SetDeletionPendingAsync(
            userId, requestedAt, scheduledFor, eligibleAfter, ct);
        if (updated)
            await userInfoInvalidator.InvalidateAsync(userId, ct);
        return updated;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var updated = await repo.ClearDeletionAsync(userId, ct);
        if (updated)
            await userInfoInvalidator.InvalidateAsync(userId, ct);
        return updated;
    }

    public Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default) =>
        repo.SetLastConsentReminderSentAsync(userId, sentAt, ct);

    // --- EventParticipation reads ---

    public Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "GetAllParticipationsForYearAsync is only meaningful through CachingUserService — " +
            "projects the year's participations from the cached UserInfo snapshot. If this is " +
            "being called on the inner UserService it indicates a DI registration mistake.");

    // --- EventParticipation writes ---

    public async Task<EventParticipation> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var persisted = await repo.UpsertParticipationAsync(
            userId, year, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared,
            declaredAt: now, checkedInAt: null, ct);

        if (persisted is null)
        {
            // Blocked by Attended.
            logger.LogWarning(
                "Cannot declare NotAttending for user {UserId} year {Year} — already Attended",
                userId, year);
            // Re-read because upsert returned null.
            return (await repo.GetParticipationAsync(userId, year, ct))!;
        }

        logger.LogInformation(
            "User {UserId} declared NotAttending for year {Year}",
            userId, year);
        return persisted;
    }

    public async Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var existing = await repo.GetParticipationAsync(userId, year, ct);
        if (existing is null)
            return false;

        if (existing.Status != ParticipationStatus.NotAttending ||
            existing.Source != ParticipationSource.UserDeclared)
        {
            logger.LogWarning(
                "Cannot undo NotAttending for user {UserId} year {Year} — status is {Status} from {Source}",
                userId, year, existing.Status, existing.Source);
            return false;
        }

        var removed = await repo.RemoveParticipationAsync(
            userId, year, ParticipationSource.UserDeclared, ct);

        if (removed)
        {
            logger.LogInformation(
                "User {UserId} undid NotAttending declaration for year {Year}",
                userId, year);
        }

        return removed;
    }

    public Task SetParticipationFromTicketSyncAsync(
        Guid userId, int year, ParticipationStatus status, Instant? checkedInAt, CancellationToken ct = default) =>
        // Attended-is-permanent, source-override and CheckedInAt-never-overwrite
        // semantics live in repo upsert.
        repo.UpsertParticipationAsync(
            userId, year, status, ParticipationSource.TicketSync,
            declaredAt: null, checkedInAt: checkedInAt, ct);

    public Task<IReadOnlyList<OnsiteUserRow>> GetOnsiteUsersAsync(int year, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "GetOnsiteUsersAsync is only meaningful through CachingUserService — it projects " +
            "Attended+CheckedInAt rows from the cached UserInfo snapshot. Hitting the inner " +
            "UserService indicates a DI registration mistake.");

    public Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default) =>
        repo.RemoveParticipationAsync(userId, year, ParticipationSource.TicketSync, ct);

    public async Task<int> BackfillParticipationsAsync(
        int year,
        List<(Guid UserId, ParticipationStatus Status)> entries,
        CancellationToken ct = default)
    {
        var count = await repo.BackfillParticipationsAsync(year, entries, ct);
        logger.LogInformation(
            "Backfilled {Count} participation records for year {Year}",
            count, year);
        return count;
    }

    // --- IUserDataContributor — GDPR export ---

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await repo.GetByIdAsync(userId, ct);
        if (user is null)
        {
            return [new UserDataSlice(GdprExportSections.Account, null)];
        }

        // see nobodies-collective/Humans#687 — User.GoogleEmail deprecated, not exported.
        var userEmails = await userEmailRepo.GetByUserIdReadOnlyAsync(userId, ct);
        var googleEmail = userEmails
            .Where(e => e.IsVerified && e.IsGoogle)
            .Select(e => e.Email)
            .FirstOrDefault();

        var shaped = new
        {
            user.Id,
            user.Email,
            user.DisplayName,
            user.PreferredLanguage,
            GoogleEmail = googleEmail,
            user.UnsubscribedFromCampaigns,
            user.SuppressScheduleChangeEmails,
            ContactSource = user.ContactSource?.ToString(),
            DeletionRequestedAt = user.DeletionRequestedAt.ToInvariantInstantString(),
            DeletionScheduledFor = user.DeletionScheduledFor.ToInvariantInstantString(),
            CreatedAt = user.CreatedAt.ToInvariantInstantString(),
            LastLoginAt = user.LastLoginAt.ToInvariantInstantString()
        };

        var participations = await repo.GetEventParticipationsByUserIdAsync(userId, ct);
        var participationsShaped = participations
            .Select(ep => new
            {
                ep.Year,
                Status = ep.Status.ToString(),
                Source = ep.Source.ToString(),
                DeclaredAt = ep.DeclaredAt.ToInvariantInstantString()
            })
            .ToList();

        return
        [
            new UserDataSlice(GdprExportSections.Account, shaped),
            new UserDataSlice(GdprExportSections.EventParticipations, participationsShaped)
        ];
    }

    private static string? GetAlternateEmail(string normalizedEmail)
    {
        if (normalizedEmail.EndsWith("@gmail.com", StringComparison.Ordinal))
            return $"{normalizedEmail[..^"@gmail.com".Length]}@googlemail.com";

        return null;
    }

    // --- Account merge: fold-into-target primitives ---

    public async Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default)
    {
        return await repo.AnonymizeForMergeAsync(sourceUserId, targetUserId, now, ct);
    }

    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        // `now` and `actorUserId` unused — kept for IUserMerge contract.
        _ = actorUserId;
        _ = now;
        await repo.ReassignLoginsToUserAsync(mergedFromUserId, mergedToUserId, ct);
        await repo.ReassignEventParticipationToUserAsync(mergedFromUserId, mergedToUserId, ct);
    }

    public Task<IReadOnlySet<Guid>> GetMergedSourceIdsAsync(
        Guid targetUserId, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "GetMergedSourceIdsAsync is only meaningful through CachingUserService — " +
            "scans the cached UserInfo snapshot for MergedToUserId tombstones. If this is " +
            "being called on the inner UserService it indicates a DI registration mistake.");

    public Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default) =>
        repo.GetUsersWithLoginsButNoEmailsAsync(ct);

    public async Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        await adminAuthorization.RequireCurrentUserIsAdminAsync(ct);
        var deleted = await repo.DeleteUsersAsync(userIds, ct);
        foreach (var userId in userIds)
            await userInfoInvalidator.InvalidateAsync(userId, ct);
        return deleted;
    }

    public Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default) =>
        repo.DeleteAllExternalLoginsForUserAsync(userId, ct);

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        repo.GetExternalLoginsByUserIdsAsync(userIds, ct);
}
