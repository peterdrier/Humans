using Humans.Application.Extensions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Users;

/// <summary>
/// Application-layer implementation of <see cref="IUserService"/>. Goes
/// through <see cref="IUserRepository"/> for all data access — this type
/// never imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph.
/// </summary>
/// <remarks>
/// Cross-section invalidation: writes that change fields exposed by
/// <see cref="FullProfile"/> (DisplayName, GoogleEmail) call
/// <see cref="IFullProfileInvalidator.InvalidateAsync"/> so the Profile cache
/// reloads the affected entry. Writes to deletion state and event
/// participation do not invalidate — those fields are not included in the
/// FullProfile projection.
/// </remarks>
public sealed class UserService : IUserService, IUserDataContributor
{
    private readonly IUserRepository _repo;
    private readonly IFullProfileInvalidator _fullProfileInvalidator;
    private readonly ITeamService _teamService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator;
    private readonly IShiftAuthorizationInvalidator _shiftAuthorizationInvalidator;
    private readonly IClock _clock;
    private readonly ILogger<UserService> _logger;

    private readonly IUserEmailRepository _userEmailRepo;

    // Lazy-resolved to avoid construction-time cycles with sections that
    // already inject IUserService themselves (IProfileService, etc.). Used
    // only by the expired-deletion anonymization path.
    private IProfileService ProfileService => _serviceProvider.GetRequiredService<IProfileService>();
    private IRoleAssignmentService RoleAssignmentService => _serviceProvider.GetRequiredService<IRoleAssignmentService>();
    private IShiftSignupService ShiftSignupService => _serviceProvider.GetRequiredService<IShiftSignupService>();
    private IShiftManagementService ShiftManagementService => _serviceProvider.GetRequiredService<IShiftManagementService>();

    public UserService(
        IUserRepository repo,
        IUserEmailRepository userEmailRepo,
        IFullProfileInvalidator fullProfileInvalidator,
        ITeamService teamService,
        IServiceProvider serviceProvider,
        IRoleAssignmentClaimsCacheInvalidator roleAssignmentClaimsInvalidator,
        IShiftAuthorizationInvalidator shiftAuthorizationInvalidator,
        IClock clock,
        ILogger<UserService> logger)
    {
        _repo = repo;
        _userEmailRepo = userEmailRepo;
        _fullProfileInvalidator = fullProfileInvalidator;
        _teamService = teamService;
        _serviceProvider = serviceProvider;
        _roleAssignmentClaimsInvalidator = roleAssignmentClaimsInvalidator;
        _shiftAuthorizationInvalidator = shiftAuthorizationInvalidator;
        _clock = clock;
        _logger = logger;
    }

    // ==========================================================================
    // User reads
    // ==========================================================================

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default) =>
        _repo.GetByIdAsync(userId, ct);

    public Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        _repo.GetByIdsAsync(userIds, ct);

    public Task<IReadOnlyDictionary<Guid, User>> GetByIdsWithEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        _repo.GetByIdsWithEmailsAsync(userIds, ct);

    public Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken ct = default) =>
        _repo.GetAllAsync(ct);

    public Task<IReadOnlyList<Guid>> GetAllUserIdsAsync(CancellationToken ct = default) =>
        _repo.GetAllUserIdsAsync(ct);

    public Task<IReadOnlyList<(string Language, int Count)>>
        GetLanguageDistributionForUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        _repo.GetLanguageDistributionForUserIdsAsync(userIds, ct);

    public async Task<(bool Purged, string? DisplayName)> PurgeAsync(
        Guid userId, CancellationToken ct = default)
    {
        var displayName = await _repo.PurgeAsync(userId, ct);
        if (displayName is null)
            return (false, null);

        // The purge renames the user + removes UserEmail rows. The FullProfile
        // cache entry must refresh so downstream consumers see the purged view.
        await _fullProfileInvalidator.InvalidateAsync(userId, ct);

        // Team summaries cache member DisplayName/ProfilePictureUrl; drop the
        // ActiveTeams cache so consumers don't keep exposing the pre-purge
        // identity until the 10-minute TTL expires. Matches the pre-#553
        // purge path and AccountMergeService/DuplicateAccountService.
        _teamService.InvalidateActiveTeamsCache();

        _logger.LogWarning("Purged human {DisplayName} ({HumanId})", displayName, userId);

        return (true, displayName);
    }

    public Task<User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default)
    {
        var normalized = EmailNormalization.NormalizeForComparison(email);
        var alternate = GetAlternateEmail(normalized);
        return _repo.GetByEmailOrAlternateAsync(normalized, alternate, ct);
    }

    public Task<IReadOnlyList<User>> GetContactUsersAsync(string? search, CancellationToken ct = default) =>
        _repo.GetContactUsersAsync(search, ct);

    public Task<IReadOnlyList<Instant>> GetLoginTimestampsInWindowAsync(
        Instant fromInclusive, Instant toExclusive, CancellationToken ct = default) =>
        _repo.GetLoginTimestampsInWindowAsync(fromInclusive, toExclusive, ct);

    public Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(
        string email, Guid excludeUserId, CancellationToken ct = default) =>
        _repo.GetOtherUserIdHavingGoogleEmailAsync(email, excludeUserId, ct);

    public Task<int> GetPendingDeletionCountAsync(CancellationToken ct = default) =>
        _repo.GetPendingDeletionCountAsync(ct);

    public Task<int> GetRejectedGoogleEmailCountAsync(CancellationToken ct = default) =>
        _repo.GetRejectedGoogleEmailCountAsync(ct);

    public Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default) =>
        _repo.GetAccountsDueForAnonymizationAsync(now, ct);

    // ==========================================================================
    // User writes
    // ==========================================================================

    public async Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default)
    {
        var set = await _repo.TrySetGoogleEmailAsync(userId, email, ct);
        if (set)
            await _fullProfileInvalidator.InvalidateAsync(userId, ct);
        return set;
    }

    public async Task<bool> SetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default)
    {
        var set = await _repo.SetGoogleEmailAsync(userId, email, ct);
        if (set)
            await _fullProfileInvalidator.InvalidateAsync(userId, ct);
        return set;
    }

    public async Task<bool> SetGoogleEmailStatusAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default)
    {
        var set = await _repo.SetGoogleEmailStatusAsync(userId, status, ct);
        if (set)
            await _fullProfileInvalidator.InvalidateAsync(userId, ct);
        return set;
    }

    public async Task<bool> TrySetGoogleEmailStatusFromSyncAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default)
    {
        if (status == GoogleEmailStatus.Valid)
        {
            var user = await _repo.GetByIdAsync(userId, ct);
            if (user is null || user.GoogleEmailStatus == GoogleEmailStatus.Rejected)
                return false;
        }

        return await SetGoogleEmailStatusAsync(userId, status, ct);
    }

    public async Task<(bool Updated, string? OldEmail)> ApplyEmailBackfillAsync(
        Guid userId, string newEmail, CancellationToken ct = default)
    {
        var (updated, oldEmail) = await _repo.RewritePrimaryEmailAsync(userId, newEmail, ct);
        if (!updated)
            return (false, null);

        // Keep the OAuth UserEmail row in lock-step so login against the new
        // provider email continues to succeed (no-op if none exists).
        await _userEmailRepo.RewriteOAuthEmailAsync(userId, newEmail, ct);
        await _fullProfileInvalidator.InvalidateAsync(userId, ct);
        return (true, oldEmail);
    }

    public async Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default)
    {
        var updated = await _repo.UpdateDisplayNameAsync(userId, displayName, ct);
        if (updated)
            await _fullProfileInvalidator.InvalidateAsync(userId, ct);
    }

    public Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default) =>
        _repo.SetDeletionPendingAsync(userId, requestedAt, scheduledFor, ct);

    public Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default) =>
        _repo.ClearDeletionAsync(userId, ct);

    public Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default) =>
        _repo.SetLastConsentReminderSentAsync(userId, sentAt, ct);

    public async Task<AnonymizedAccountSummary?> AnonymizeExpiredAccountAsync(
        Guid userId, Instant now, CancellationToken ct = default)
    {
        // Capture the identity slice BEFORE any writes so the caller can send
        // the confirmation email / emit audit entries even if the User-aggregate
        // anonymization (the final step) is the place that fails.
        var user = await _repo.GetByIdAsync(userId, ct);
        if (user is null)
            return null;

        var originalEmail = user.GetEffectiveEmail();
        var originalDisplayName = user.DisplayName;
        var preferredLanguage = user.PreferredLanguage;

        // Do every cross-section cleanup FIRST, while the account is still
        // marked for deletion. If any of these throws, the DeletionScheduledFor /
        // DeletionEligibleAfter fields are still set, so tomorrow's job run
        // retries the same user rather than silently leaving them in a
        // half-anonymized state. Only when every cleanup has committed do we
        // collapse the User identity (which is the step that clears the
        // deletion markers).

        // 1. End team memberships and team role slot assignments.
        await _teamService.RevokeAllMembershipsAsync(userId, ct);

        // 2. End active governance role assignments.
        await RoleAssignmentService.RevokeAllActiveAsync(userId, ct);

        // 3. Anonymize the profile + remove contact fields + volunteer history.
        await ProfileService.AnonymizeExpiredProfileAsync(userId, ct);

        // 4. Cancel active shift signups (returns ids for per-signup audit log).
        var cancelledSignupIds = await ShiftSignupService.CancelActiveSignupsForUserAsync(
            userId, "Account deletion", ct);

        // 5. Delete the user's VolunteerEventProfile row(s).
        await ShiftManagementService.DeleteShiftProfilesForUserAsync(userId, ct);

        // 6. Finally, anonymize identity + remove UserEmails on the User
        //    aggregate. This is the write that clears DeletionScheduledFor /
        //    DeletionEligibleAfter — once this commits, the user falls off
        //    tomorrow's candidate list.
        var identity = await _repo.ApplyExpiredDeletionAnonymizationAsync(userId, ct);
        if (identity is null)
        {
            // Can only happen if the user was deleted by another code path
            // between the GetById above and now. Still return the captured
            // pre-write slice so the caller can audit the cancelled signups
            // / pre-existing identity.
            return new AnonymizedAccountSummary(
                originalEmail, originalDisplayName, preferredLanguage, cancelledSignupIds);
        }

        // 7. Invalidate cross-cutting caches that key off the user.
        await _fullProfileInvalidator.InvalidateAsync(userId, ct);
        _teamService.RemoveMemberFromAllTeamsCache(userId);
        _roleAssignmentClaimsInvalidator.Invalidate(userId);
        _shiftAuthorizationInvalidator.Invalidate(userId);

        return new AnonymizedAccountSummary(
            identity.OriginalEmail,
            identity.OriginalDisplayName,
            identity.PreferredLanguage,
            cancelledSignupIds);
    }

    // ==========================================================================
    // EventParticipation reads
    // ==========================================================================

    public Task<EventParticipation?> GetParticipationAsync(Guid userId, int year, CancellationToken ct = default) =>
        _repo.GetParticipationAsync(userId, year, ct);

    public async Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default)
    {
        var list = await _repo.GetAllParticipationsForYearAsync(year, ct);
        return list.ToList();
    }

    // ==========================================================================
    // EventParticipation writes — apply business rules, delegate persistence
    // ==========================================================================

    public async Task<EventParticipation> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var persisted = await _repo.UpsertParticipationAsync(
            userId, year, ParticipationStatus.NotAttending, ParticipationSource.UserDeclared, now, ct);

        if (persisted is null)
        {
            // Blocked by Attended — return the existing (unchanged) row.
            _logger.LogWarning(
                "Cannot declare NotAttending for user {UserId} year {Year} — already Attended",
                userId, year);
            // Caller sees the current state; re-read because upsert returned null without the entity.
            return (await _repo.GetParticipationAsync(userId, year, ct))!;
        }

        _logger.LogInformation(
            "User {UserId} declared NotAttending for year {Year}",
            userId, year);
        return persisted;
    }

    public async Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var existing = await _repo.GetParticipationAsync(userId, year, ct);
        if (existing is null)
            return false;

        if (existing.Status != ParticipationStatus.NotAttending ||
            existing.Source != ParticipationSource.UserDeclared)
        {
            _logger.LogWarning(
                "Cannot undo NotAttending for user {UserId} year {Year} — status is {Status} from {Source}",
                userId, year, existing.Status, existing.Source);
            return false;
        }

        var removed = await _repo.RemoveParticipationAsync(
            userId, year, ParticipationSource.UserDeclared, ct);

        if (removed)
        {
            _logger.LogInformation(
                "User {UserId} undid NotAttending declaration for year {Year}",
                userId, year);
        }

        return removed;
    }

    public Task SetParticipationFromTicketSyncAsync(
        Guid userId, int year, ParticipationStatus status, CancellationToken ct = default) =>
        // Attended-is-permanent and source override semantics live in the repo upsert.
        _repo.UpsertParticipationAsync(userId, year, status, ParticipationSource.TicketSync, declaredAt: null, ct);

    public Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default) =>
        _repo.RemoveParticipationAsync(userId, year, ParticipationSource.TicketSync, ct);

    public async Task<int> BackfillParticipationsAsync(
        int year,
        List<(Guid UserId, ParticipationStatus Status)> entries,
        CancellationToken ct = default)
    {
        var count = await _repo.BackfillParticipationsAsync(year, entries, ct);
        _logger.LogInformation(
            "Backfilled {Count} participation records for year {Year}",
            count, year);
        return count;
    }

    // ==========================================================================
    // IUserDataContributor — GDPR export
    // ==========================================================================

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await _repo.GetByIdAsync(userId, ct);
        if (user is null)
        {
            return [new UserDataSlice(GdprExportSections.Account, null)];
        }

        var shaped = new
        {
            user.Id,
            user.Email,
            user.DisplayName,
            user.PreferredLanguage,
            user.GoogleEmail,
            user.UnsubscribedFromCampaigns,
            user.SuppressScheduleChangeEmails,
            ContactSource = user.ContactSource?.ToString(),
            DeletionRequestedAt = user.DeletionRequestedAt.ToInvariantInstantString(),
            DeletionScheduledFor = user.DeletionScheduledFor.ToInvariantInstantString(),
            CreatedAt = user.CreatedAt.ToInvariantInstantString(),
            LastLoginAt = user.LastLoginAt.ToInvariantInstantString()
        };

        return [new UserDataSlice(GdprExportSections.Account, shaped)];
    }

    private static string? GetAlternateEmail(string normalizedEmail)
    {
        if (normalizedEmail.EndsWith("@gmail.com", StringComparison.Ordinal))
            return $"{normalizedEmail[..^"@gmail.com".Length]}@googlemail.com";

        return null;
    }

    public Task<IReadOnlyList<(Guid UserId, string DisplayName, string GoogleEmail)>>
        BackfillNobodiesTeamGoogleEmailsAsync(CancellationToken ct = default) =>
        _repo.BackfillNobodiesTeamGoogleEmailsAsync(ct);
}
