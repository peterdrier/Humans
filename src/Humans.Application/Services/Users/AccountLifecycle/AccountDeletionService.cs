using Humans.Application.Extensions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Users.AccountLifecycle;

/// <summary>
/// Orchestrates the user/profile deletion cascade. See
/// <see cref="IAccountDeletionService"/> for the shape and rationale.
/// </summary>
/// <remarks>
/// <para>
/// Foundational services (<see cref="IUserService"/>, <see cref="IProfileService"/>)
/// must not reach up into higher-level sections — Teams, RoleAssignments,
/// Shifts all sit above them in the section ownership graph (see
/// <c>project_section_ownership</c> memory + <c>feedback_user_profile_foundational</c>).
/// Placing the cascade here keeps User/Profile dependency-free of those
/// sections and gives the Hangfire deletion job a single entry point.
/// </para>
/// <para>
/// Lazy-resolves <see cref="IProfileService"/> via <see cref="IServiceProvider"/>
/// to break the Profile ↔ AccountDeletion DI cycle: <see cref="IProfileService"/>
/// eagerly injects <see cref="IAccountDeletionService"/> (so that
/// <c>CachingProfileService</c> can fire post-success cache side effects on
/// <c>RequestDeletionAsync</c>), so this service must lazy-resolve
/// <see cref="IProfileService"/> back to invoke the cascade's
/// profile-anonymization step. See <c>docs/architecture/dependency-graph.md</c>
/// cycle #2.
/// </para>
/// </remarks>
public sealed class AccountDeletionService : IAccountDeletionService
{
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IShiftSignupService _shiftSignupService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator;
    private readonly IShiftAuthorizationInvalidator _shiftAuthorizationInvalidator;
    private readonly IAuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly IClock _clock;
    private readonly ILogger<AccountDeletionService> _logger;

    // Lazy-resolved to break the Profile ↔ AccountDeletion DI cycle (see
    // class remarks). Only called on the expiry-cascade path, so the extra
    // resolve cost is irrelevant.
    private IProfileService ProfileService => _serviceProvider.GetRequiredService<IProfileService>();

    public AccountDeletionService(
        IUserService userService,
        IUserEmailService userEmailService,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        IShiftSignupService shiftSignupService,
        IShiftManagementService shiftManagementService,
        IServiceProvider serviceProvider,
        IRoleAssignmentClaimsCacheInvalidator roleAssignmentClaimsInvalidator,
        IShiftAuthorizationInvalidator shiftAuthorizationInvalidator,
        IAuditLogService auditLogService,
        IEmailService emailService,
        IClock clock,
        ILogger<AccountDeletionService> logger)
    {
        _userService = userService;
        _userEmailService = userEmailService;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _shiftSignupService = shiftSignupService;
        _shiftManagementService = shiftManagementService;
        _serviceProvider = serviceProvider;
        _roleAssignmentClaimsInvalidator = roleAssignmentClaimsInvalidator;
        _shiftAuthorizationInvalidator = shiftAuthorizationInvalidator;
        _auditLogService = auditLogService;
        _emailService = emailService;
        _clock = clock;
        _logger = logger;
    }

    // ==========================================================================
    // User-initiated deletion request (30-day scheduled)
    // ==========================================================================

    public async Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null)
            return new OnboardingResult(false, "NotFound");

        if (user.IsDeletionPending)
            return new OnboardingResult(false, "AlreadyPending");

        var now = _clock.GetCurrentInstant();
        var deletionDate = now.Plus(Duration.FromDays(30));

        // 1. Persist deletion-pending fields on User.
        await _userService.SetDeletionPendingAsync(userId, now, deletionDate, ct);

        // 2. Revoke team memberships and team role assignments immediately so
        //    the user loses access during the 30-day grace period.
        var endedMemberships = await _teamService.RevokeAllMembershipsAsync(userId, ct);

        // 3. Revoke governance role assignments.
        var endedRoles = await _roleAssignmentService.RevokeAllActiveAsync(userId, ct);

        // 4. Audit log.
        await _auditLogService.LogAsync(
            AuditAction.MembershipsRevokedOnDeletionRequest, nameof(User), userId,
            $"Revoked {endedMemberships} team membership(s) and {endedRoles} role assignment(s) on deletion request",
            userId);

        _logger.LogWarning(
            "User {UserId} requested account deletion. Scheduled for {DeletionDate}. " +
            "Revoked {MembershipCount} memberships and {RoleCount} roles immediately",
            userId, deletionDate, endedMemberships, endedRoles);

        // 5. Send deletion confirmation email. Route through IUserEmailService —
        //    user_emails is a Profiles-section table, so we cross via the
        //    section's service interface rather than the repository directly.
        var notificationEmails = await _userEmailService.GetNotificationTargetEmailsAsync([userId], ct);
        var notificationEmail = notificationEmails.GetValueOrDefault(userId) ?? user.Email;
        if (notificationEmail is not null)
        {
            await _emailService.SendAccountDeletionRequestedAsync(
                notificationEmail,
                user.DisplayName,
                deletionDate.ToDateTimeUtc(),
                user.PreferredLanguage,
                ct);
        }

        // 6. Drop the shift-authorization cache so coordinator privilege
        //    reverts immediately rather than waiting out the 60-second TTL.
        //    Parity with PurgeAsync / AnonymizeExpiredAccountAsync — keeps the
        //    invalidation co-located with the orchestrating mutation so direct
        //    callers of IAccountDeletionService don't depend on routing through
        //    the Profile caching decorator for correctness.
        _shiftAuthorizationInvalidator.Invalidate(userId);

        return new OnboardingResult(true);
    }

    // ==========================================================================
    // Admin-initiated immediate purge
    // ==========================================================================

    public async Task<OnboardingResult> PurgeAsync(Guid userId, Guid? actorId = null, CancellationToken ct = default)
    {
        // Purge is identity-only at the User aggregate — renames + drops
        // UserEmail rows and locks out the account. Own-data deletion lives
        // in IUserService.PurgeOwnDataAsync.
        var displayName = await _userService.PurgeOwnDataAsync(userId, ct);
        if (displayName is null)
            return new OnboardingResult(false, "NotFound");

        // Team summaries cache member DisplayName/ProfilePictureUrl; drop the
        // ActiveTeams cache so consumers don't keep exposing the pre-purge
        // identity until the 10-minute TTL expires. Deletion-specific
        // invalidation — belongs here, not in UserService.
        _teamService.InvalidateActiveTeamsCache();

        // Match the cache-invalidation surface of AnonymizeExpiredAccountAsync
        // so the admin purge path doesn't leave stale per-user caches behind:
        // role-assignment claims (used by authorization handlers) and the
        // shift-authorization cache (60 s TTL on shift-coordinator privilege).
        _roleAssignmentClaimsInvalidator.Invalidate(userId);
        _shiftAuthorizationInvalidator.Invalidate(userId);

        // GDPR audit trail: admin-initiated purge is irreversible identity
        // collapse. Record the actor + the pre-purge display name so a
        // subsequent right-of-access request can answer "when was my data
        // erased and by whom?". (Right-of-access reads the audit log.)
        var description = $"Admin-initiated purge: identity collapsed (was \"{displayName}\")";
        if (actorId is Guid actor)
        {
            await _auditLogService.LogAsync(
                AuditAction.AccountPurged, nameof(User), userId, description, actor);
        }
        else
        {
            await _auditLogService.LogAsync(
                AuditAction.AccountPurged, nameof(User), userId, description,
                jobName: nameof(AccountDeletionService));
        }

        return new OnboardingResult(true);
    }

    // ==========================================================================
    // Expiry-triggered anonymization (scheduled job)
    // ==========================================================================

    public async Task<AnonymizedAccountSummary?> AnonymizeExpiredAccountAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Capture the identity slice BEFORE any writes so the caller can send
        // the confirmation email / emit audit entries even if the User-aggregate
        // anonymization (the final step) is the place that fails.
        var user = await _userService.GetByIdAsync(userId, ct);
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
        await _roleAssignmentService.RevokeAllActiveAsync(userId, ct);

        // 3. Anonymize the profile + remove contact fields + volunteer history.
        await ProfileService.AnonymizeExpiredProfileAsync(userId, ct);

        // 4. Cancel active shift signups (returns ids for per-signup audit log).
        var cancelledSignupIds = await _shiftSignupService.CancelActiveSignupsForUserAsync(
            userId, "Account deletion", ct);

        // 5. Delete the user's VolunteerEventProfile row(s).
        await _shiftManagementService.DeleteShiftProfilesForUserAsync(userId, ct);

        // 6. Finally, anonymize identity + remove UserEmails on the User
        //    aggregate. This is the write that clears DeletionScheduledFor /
        //    DeletionEligibleAfter — once this commits, the user falls off
        //    tomorrow's candidate list. UserService owns the repo call and
        //    FullProfile invalidation; cross-section cache invalidation
        //    (below) stays with the orchestrator.
        var identity = await _userService.ApplyExpiredDeletionAnonymizationAsync(userId, ct);
        if (identity is null)
        {
            // Can only happen if the user was deleted by another code path
            // between the GetById above and now. Steps 1–5 already committed
            // and each owning section invalidated its own caches as part of
            // those writes; the step-7 cross-section invalidations below key
            // off the identity write completing, so we skip them here. Still
            // return the captured pre-write slice so the caller can audit the
            // cancelled signups / pre-existing identity.
            return new AnonymizedAccountSummary(
                originalEmail, originalDisplayName, preferredLanguage, cancelledSignupIds);
        }

        // 7. Invalidate cross-section caches that key off the user. The
        //    FullProfile entry was already invalidated by UserService when it
        //    wrote the User-aggregate anonymization; the ones here belong to
        //    sections other than Users.
        _teamService.RemoveMemberFromAllTeamsCache(userId);
        _roleAssignmentClaimsInvalidator.Invalidate(userId);
        _shiftAuthorizationInvalidator.Invalidate(userId);

        return new AnonymizedAccountSummary(
            identity.OriginalEmail,
            identity.OriginalDisplayName,
            identity.PreferredLanguage,
            cancelledSignupIds);
    }
}
