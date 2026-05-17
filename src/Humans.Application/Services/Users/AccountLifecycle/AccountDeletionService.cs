using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Users.AccountLifecycle;

// Orchestrates user/profile deletion cascade — sits above User/Profile so foundational services stay dependency-free of Teams/Shifts/Tickets.
public sealed class AccountDeletionService : IAccountDeletionService
{
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IShiftSignupService _shiftSignupService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly IProfileService _profileService;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator;
    private readonly IShiftAuthorizationInvalidator _shiftAuthorizationInvalidator;
    private readonly IShiftViewInvalidator _shiftViewInvalidator;
    private readonly IAuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly IClock _clock;
    private readonly ILogger<AccountDeletionService> _logger;

    public AccountDeletionService(
        IUserService userService,
        IUserEmailService userEmailService,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        IShiftSignupService shiftSignupService,
        IShiftManagementService shiftManagementService,
        IProfileService profileService,
        ITicketQueryService ticketQueryService,
        IRoleAssignmentClaimsCacheInvalidator roleAssignmentClaimsInvalidator,
        IShiftAuthorizationInvalidator shiftAuthorizationInvalidator,
        IShiftViewInvalidator shiftViewInvalidator,
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
        _profileService = profileService;
        _ticketQueryService = ticketQueryService;
        _roleAssignmentClaimsInvalidator = roleAssignmentClaimsInvalidator;
        _shiftAuthorizationInvalidator = shiftAuthorizationInvalidator;
        _shiftViewInvalidator = shiftViewInvalidator;
        _auditLogService = auditLogService;
        _emailService = emailService;
        _clock = clock;
        _logger = logger;
    }

    // --- User-initiated deletion request (30-day scheduled) ---

    public async Task<DeletionRequestResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null)
            return new DeletionRequestResult(false, "NotFound");

        if (user.IsDeletionPending)
            return new DeletionRequestResult(false, "AlreadyPending");

        var now = _clock.GetCurrentInstant();
        var deletionDate = now.Plus(Duration.FromDays(30));

        // Ticket hold: held until after the event so the ticket remains usable.
        Instant? eligibleAfter = null;
        if (await _ticketQueryService.HasCurrentEventTicketAsync(userId, ct))
        {
            eligibleAfter = await _ticketQueryService.GetPostEventHoldDateAsync(ct);
        }

        // 1. Persist deletion-pending fields on User.
        await _userService.SetDeletionPendingAsync(userId, now, deletionDate, eligibleAfter, ct);

        // 2. Revoke team memberships immediately — user loses access during grace period.
        var endedMemberships = await _teamService.RevokeAllMembershipsAsync(userId, ct);

        // 3. Revoke governance roles.
        var endedRoles = await _roleAssignmentService.RevokeAllActiveAsync(userId, ct);

        // 4. Audit.
        await _auditLogService.LogAsync(
            AuditAction.MembershipsRevokedOnDeletionRequest, nameof(User), userId,
            $"Revoked {endedMemberships} team membership(s) and {endedRoles} role assignment(s) on deletion request",
            userId);

        _logger.LogWarning(
            "User {UserId} requested account deletion. Scheduled for {DeletionDate} (eligibleAfter {EligibleAfter}). " +
            "Revoked {MembershipCount} memberships and {RoleCount} roles immediately",
            userId, deletionDate, eligibleAfter, endedMemberships, endedRoles);

        // 5. Send deletion confirmation email.
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

        // 6. Drop shift-authorization cache so coordinator privilege reverts immediately (parity with Purge/AnonymizeExpired).
        _shiftAuthorizationInvalidator.Invalidate(userId);
        _shiftViewInvalidator.InvalidateUser(userId);

        return new DeletionRequestResult(
            Success: true,
            EffectiveDeletionDate: eligibleAfter ?? deletionDate,
            IsHeldForTicket: eligibleAfter is not null);
    }

    public async Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null)
            return new OnboardingResult(false, "NotFound");

        if (!user.IsDeletionPending)
            return new OnboardingResult(false, "NoDeletionPending");

        await _userService.ClearDeletionAsync(userId, ct);

        _logger.LogInformation("User {UserId} cancelled account deletion request", userId);

        return new OnboardingResult(true);
    }

    // --- Admin-initiated immediate purge ---

    public async Task<OnboardingResult> PurgeAsync(Guid userId, Guid? actorId = null, CancellationToken ct = default)
    {
        // Identity-only at the User aggregate; own-data delete in IUserService.PurgeOwnDataAsync.
        var displayName = await _userService.PurgeOwnDataAsync(userId, ct);
        if (displayName is null)
            return new OnboardingResult(false, "NotFound");

        // Sever external logins so OAuth sign-in creates a fresh user.
        await _userService.DeleteAllExternalLoginsForUserAsync(userId, ct);

        // Drop ActiveTeams cache so consumers don't expose pre-purge identity until TTL.
        _teamService.InvalidateActiveTeamsCache();

        // Match AnonymizeExpiredAccountAsync's invalidation surface.
        _roleAssignmentClaimsInvalidator.Invalidate(userId);
        _shiftAuthorizationInvalidator.Invalidate(userId);
        _shiftViewInvalidator.InvalidateUser(userId);

        // GDPR audit — right-of-access reads from the audit log.
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

    // --- Expiry-triggered anonymization (scheduled job) ---

    public async Task<AnonymizedAccountSummary?> AnonymizeExpiredAccountAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Capture identity slice BEFORE any writes — caller still needs it if the final step throws.
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null)
            return null;

        var originalEmail = user.Email;
        var originalDisplayName = user.DisplayName;
        var preferredLanguage = user.PreferredLanguage;

        // Cross-section cleanup BEFORE identity collapse — deletion markers stay set so a failure retries tomorrow.

        // 1. End team memberships + role slots.
        await _teamService.RevokeAllMembershipsAsync(userId, ct);

        // 2. End governance roles.
        await _roleAssignmentService.RevokeAllActiveAsync(userId, ct);

        // 3. Anonymize profile + contact fields + volunteer history.
        await _profileService.AnonymizeExpiredProfileAsync(userId, ct);

        // 4. Cancel active shift signups.
        var cancelledSignupIds = await _shiftSignupService.CancelActiveSignupsForUserAsync(
            userId, "Account deletion", ct);

        // 5. Delete VolunteerEventProfile rows.
        await _shiftManagementService.DeleteShiftProfilesForUserAsync(userId, ct);

        // 6. Anonymize identity + drop UserEmails — clears deletion markers; user falls off the candidate list.
        var identity = await _userService.ApplyExpiredDeletionAnonymizationAsync(userId, ct);
        if (identity is null)
        {
            // Concurrent deletion — steps 1–5 already invalidated their own caches; skip step-7 and return the captured slice.
            return new AnonymizedAccountSummary(
                originalEmail, originalDisplayName, preferredLanguage, cancelledSignupIds);
        }

        // 7. Cross-section cache invalidations (UserInfo already done by UserService).
        _teamService.RemoveMemberFromAllTeamsCache(userId);
        _roleAssignmentClaimsInvalidator.Invalidate(userId);
        _shiftAuthorizationInvalidator.Invalidate(userId);
        _shiftViewInvalidator.InvalidateUser(userId);

        return new AnonymizedAccountSummary(
            identity.OriginalEmail,
            identity.OriginalDisplayName,
            identity.PreferredLanguage,
            cancelledSignupIds);
    }
}
