using Humans.Base.Attributes;
using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Application.Services.Users.AccountLifecycle;

// Orchestrates user/profile deletion cascade — sits above User/Profile so foundational services stay dependency-free of Teams/Shifts/Tickets.
[CrossSectionWrite("GDPR erasure revokes the user's team memberships and early-entry grants.")]
internal sealed class AccountDeletionService(
    IUserService userService,
    // Merge-chain resolution goes through the read contract, matching every other caller of
    // GetMergedSourceIdsAsync (AuditLog, Consent, Budget) — the primitive is only answerable
    // by the caching decorator, which is what IUserServiceRead resolves to.
    IUserServiceRead userServiceRead,
    IUserEmailService userEmailService,
    ITeamService teamService,
    IRoleAssignmentService roleAssignmentService,
    IGdprService gdprService,
    ITicketServiceRead ticketQueryService,
    IUserInfoInvalidator userInfoInvalidator,
    IRoleAssignmentClaimsCacheInvalidator roleAssignmentClaimsInvalidator,
    IShiftAuthorizationInvalidator shiftAuthorizationInvalidator,
    IShiftViewInvalidator shiftViewInvalidator,
    IAuditLogService auditLogService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    IClock clock,
    ILogger<AccountDeletionService> logger) : IAccountDeletionService
{
    // --- User-initiated deletion request (30-day scheduled) ---

    public async Task<DeletionRequestResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userService.GetUserInfoAsync(userId, ct);
        if (user is null)
            return new DeletionRequestResult(false, "NotFound");

        if (user.IsDeletionPending)
            return new DeletionRequestResult(false, "AlreadyPending");

        var now = clock.GetCurrentInstant();
        var deletionDate = now.Plus(Duration.FromDays(30));

        // Ticket hold: held until after the event so the ticket remains usable.
        Instant? eligibleAfter = null;
        var ticketHoldings = await ticketQueryService.GetUserTicketHoldingsAsync(userId, ct);
        if (ticketHoldings.HasCurrentEventTicket)
        {
            eligibleAfter = ticketHoldings.PostEventHoldDate;
        }

        // 1. Persist deletion-pending fields on User.
        await userService.SetDeletionPendingAsync(userId, now, deletionDate, eligibleAfter, ct);

        // 2. Revoke team memberships immediately — user loses access during grace period.
        var endedMemberships = await teamService.RevokeAllMembershipsAsync(userId, ct);

        // 3. Revoke governance roles.
        var endedRoles = await roleAssignmentService.RevokeAllActiveAsync(userId, ct);

        // 4. Audit.
        await auditLogService.LogAsync(
            AuditAction.MembershipsRevokedOnDeletionRequest, nameof(User), userId,
            $"Revoked {endedMemberships} team membership(s) and {endedRoles} role assignment(s) on deletion request",
            userId);

        logger.LogWarning(
            "User {UserId} requested account deletion. Scheduled for {DeletionDate} (eligibleAfter {EligibleAfter}). " +
            "Revoked {MembershipCount} memberships and {RoleCount} roles immediately",
            userId, deletionDate, eligibleAfter, endedMemberships, endedRoles);

        // 5. Send deletion confirmation email.
        var notificationEmails = await userEmailService.GetNotificationTargetEmailsAsync([userId], ct);
        var notificationEmail = notificationEmails.GetValueOrDefault(userId) ?? user.Email;
        if (notificationEmail is not null)
        {
            await emailService.SendAsync(emailMessages.AccountDeletionRequested(
                notificationEmail,
                user.BurnerName,
                deletionDate,
                user.PreferredLanguage),
                ct);
        }

        // 6. Drop shift-authorization cache so coordinator privilege reverts immediately (parity with Purge/AnonymizeExpired).
        shiftAuthorizationInvalidator.Invalidate(userId);
        shiftViewInvalidator.InvalidateUser(userId);

        return new DeletionRequestResult(
            Success: true,
            EffectiveDeletionDate: eligibleAfter ?? deletionDate,
            IsHeldForTicket: eligibleAfter is not null);
    }

    public async Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userService.GetUserInfoAsync(userId, ct);
        if (user is null)
            return new OnboardingResult(false, "NotFound");

        if (!user.IsDeletionPending)
            return new OnboardingResult(false, "NoDeletionPending");

        await userService.ClearDeletionAsync(userId, ct);

        logger.LogInformation("User {UserId} cancelled account deletion request", userId);

        return new OnboardingResult(true);
    }

    // --- Admin-initiated immediate purge ---

    public async Task<OnboardingResult> PurgeAsync(Guid userId, Guid? actorId = null, CancellationToken ct = default)
    {
        if (await userService.GetUserInfoAsync(userId, ct) is null)
            return new OnboardingResult(false, "NotFound");

        // Same Article 17 fan-out as the expiry path — an admin purge must not
        // erase less than the scheduled job does. The Account contributor inside
        // it owns the identity collapse (tombstone name and address, external
        // logins, permanent lockout); a second pass here would capture the
        // tombstone instead of the real name and break IsGdprAnonymized.
        await EraseEverySectionAsync(userId, ct);

        // Drop ActiveTeams cache so consumers don't expose pre-purge identity until TTL.
        teamService.InvalidateActiveTeamsCache();

        // Match AnonymizeExpiredAccountAsync's invalidation surface — contributors
        // run against the inner UserService, so nothing behind the caching
        // decorator has seen the collapse yet.
        await userInfoInvalidator.InvalidateAsync(userId, ct);
        roleAssignmentClaimsInvalidator.Invalidate(userId);
        shiftAuthorizationInvalidator.Invalidate(userId);
        shiftViewInvalidator.InvalidateUser(userId);

        // GDPR audit — right-of-access reads from the audit log. Like the scheduled
        // path, the description must not name the human: the audit log survives
        // erasure, so quoting the purged identity here would put it straight back.
        const string description = "Admin-initiated purge: identity collapsed";
        if (actorId is Guid actor)
        {
            await auditLogService.LogAsync(
                AuditAction.AccountPurged, nameof(User), userId, description, actor);
        }
        else
        {
            await auditLogService.LogAsync(
                AuditAction.AccountPurged, nameof(User), userId, description,
                jobName: nameof(AccountDeletionService));
        }

        return new OnboardingResult(true);
    }

    // --- Expiry-triggered anonymization (scheduled job) ---

    public async Task<AnonymizedAccountSummary?> AnonymizeExpiredAccountAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Capture identity slice BEFORE any writes — caller still needs it if the cascade throws.
        var user = await userService.GetUserInfoAsync(userId, ct);
        if (user is null)
            return null;

        var summary = new AnonymizedAccountSummary(
            user.Email, user.BurnerName, user.PreferredLanguage);

        // Deletion markers stay set until the last contributor succeeds, so a
        // mid-cascade failure retries the whole fan-out tomorrow.
        await EraseEverySectionAsync(userId, ct);

        // Cross-section cache invalidations (each contributor drops its own; these are the shared ones).
        // The UserInfo entry first: contributors are registered against the inner
        // UserService, so nothing behind the caching decorator has seen these writes
        // and admin search would keep matching the erased human by their real name.
        await userInfoInvalidator.InvalidateAsync(userId, ct);
        teamService.RemoveMemberFromAllTeamsCache(userId);
        teamService.InvalidateActiveTeamsCache();
        roleAssignmentClaimsInvalidator.Invalidate(userId);
        shiftAuthorizationInvalidator.Invalidate(userId);
        shiftViewInvalidator.InvalidateUser(userId);

        return summary;
    }

    // --- GDPR Article 17 fan-out (delegated to Gdpr) ---

    /// <summary>
    /// Erases every section's personal data for the account and for every account
    /// previously merged into it. Resolves the merge chain here and calls
    /// <see cref="IGdprService.EraseForUserAsync"/> per id — the loop over contributors
    /// lives in Gdpr now, beside the export fan-out — invalidating each archived id's
    /// cache entry as its erasure completes.
    /// </summary>
    /// <remarks>
    /// Merged-source ids are erased first, and only then the survivor. Merge moves rows for
    /// the sections that implement <see cref="IUserMerge"/>; the ones that do not leave their
    /// rows keyed to the archived source id, where erasing the survivor alone would never
    /// reach them. To the human there was only ever one account, so all of it is their data.
    /// <para>
    /// Invalidation is interleaved with erasure, not batched after it: a contributor that
    /// throws partway through the chain still leaves every id erased before it with its
    /// cache dropped — an admin <see cref="PurgeAsync"/> has no daily retry to fix it later.
    /// </para>
    /// </remarks>
    private async Task EraseEverySectionAsync(Guid userId, CancellationToken ct)
    {
        foreach (var subjectId in await MergeChainAsync(userId, ct))
        {
            await gdprService.EraseForUserAsync(subjectId, ct);

            // An archived id's own cache entry, dropped here: contributors write through the
            // inner UserService, and the callers only invalidate the survivor. A stale entry
            // would keep the merge tombstone's name searchable after it was erased.
            if (subjectId != userId)
                await userInfoInvalidator.InvalidateAsync(subjectId, ct);
        }
    }

    /// <summary>
    /// Every archived id folded into <paramref name="userId"/>, with the survivor itself
    /// last. Walks transitively — an A→B→C chain leaves A pointing at B —
    /// over the single canonical primitive
    /// (<see cref="IUserServiceRead.GetMergedSourceIdsAsync"/>). Typically returns one id.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> MergeChainAsync(Guid userId, CancellationToken ct)
    {
        var sources = new List<Guid>();
        var seen = new HashSet<Guid> { userId };
        var frontier = new Queue<Guid>([userId]);

        while (frontier.Count > 0)
        {
            foreach (var sourceId in await userServiceRead.GetMergedSourceIdsAsync(frontier.Dequeue(), ct))
            {
                if (!seen.Add(sourceId)) continue;
                sources.Add(sourceId);
                frontier.Enqueue(sourceId);
            }
        }

        return [.. sources, userId];
    }
}
