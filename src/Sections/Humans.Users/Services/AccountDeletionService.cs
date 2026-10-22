using Humans.Base.Extensions;
using Humans.Users.Services;
using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Hosting;
using NodaTime;

namespace Humans.Application.Services.Users.AccountLifecycle;

// Sits above User/Profile so the foundational services stay free of Teams/Shifts/Tickets.
internal sealed class AccountDeletionService(
    IUserManagement userService,
    IUserEmailService userEmailService,
    ITeamWrite teamService,
    IRoleAssignmentWrite roleAssignmentService,
    IGdprService gdprService,
    ITicketService ticketQueryService,
    IUserInfoSliceRefresher userInfoRefresher,
    IAuditLogService auditLogService,
    IEmailService emailService,
    UsersEmails emailMessages,
    IClock clock,
    IHostEnvironment environment,
    ILogger<AccountDeletionService> logger) : IAccountDeletionService, IOrchestrator
{
    /// <summary>Purge is a dev/QA tool, never run in production.</summary>
    internal const string PurgeRefusedInProduction = "Production";

    /// <summary>An admin may not purge their own account.</summary>
    internal const string PurgeRefusedSelf = "Self";

    public async Task<DeletionRequestResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userService.GetRawUserInfoAsync(userId, ct);
        if (user is null)
            return new DeletionRequestResult(false, "NotFound");

        if (user.IsDeletionPending)
            return new DeletionRequestResult(false, "AlreadyPending");

        var now = clock.GetCurrentInstant();
        var deletionDate = now.Plus(Duration.FromDays(30));

        // Held until after the event so the ticket stays usable.
        Instant? eligibleAfter = null;
        var ticketHoldings = await ticketQueryService.GetUserTicketHoldingsAsync(userId, ct);
        if (ticketHoldings.HasCurrentEventTicket)
        {
            eligibleAfter = ticketHoldings.PostEventHoldDate;
        }

        var effectiveDeletionDate = eligibleAfter is { } holdDate && holdDate > deletionDate
            ? holdDate : deletionDate;

        await userService.SetDeletionPendingAsync(userId, now, deletionDate, eligibleAfter, ct);

        // Access ends during the grace period.
        var endedMemberships = await teamService.RevokeAllMembershipsAsync(userId, ct);

        var endedRoles = await roleAssignmentService.RevokeAllActiveAsync(userId, ct);

        await auditLogService.LogAsync(
            AuditAction.MembershipsRevokedOnDeletionRequest, nameof(User), userId,
            $"Revoked {endedMemberships} team membership(s) and {endedRoles} role assignment(s) on deletion request",
            userId);

        logger.LogWarning(
            "User {UserId} requested account deletion. Scheduled for {DeletionDate} (eligibleAfter {EligibleAfter}). " +
            "Revoked {MembershipCount} memberships and {RoleCount} roles immediately",
            userId, deletionDate, eligibleAfter, endedMemberships, endedRoles);

        var notificationEmails = await userEmailService.GetNotificationTargetEmailsAsync([userId], ct);
        var notificationEmail = notificationEmails.GetValueOrDefault(userId) ?? user.Email;
        if (notificationEmail is not null)
        {
            await emailService.SendAsync(emailMessages.AccountDeletionRequested(
                userId,
                notificationEmail,
                user.BurnerName,
                effectiveDeletionDate,
                user.PreferredLanguage.IsSupportedCultureCode()
                    ? user.PreferredLanguage : CultureCatalog.DefaultCultureCode),
                ct);
        }

        return new DeletionRequestResult(
            Success: true,
            EffectiveDeletionDate: effectiveDeletionDate,
            IsHeldForTicket: eligibleAfter is not null);
    }

    public async Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userService.GetRawUserInfoAsync(userId, ct);
        if (user is null)
            return new OnboardingResult(false, "NotFound");

        if (!user.IsDeletionPending)
            return new OnboardingResult(false, "NoDeletionPending");

        await userService.ClearDeletionAsync(userId, ct);

        logger.LogInformation("User {UserId} cancelled account deletion request", userId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> PurgeAsync(Guid userId, Guid? actorId = null, CancellationToken ct = default)
    {
        if (environment.IsProduction())
        {
            logger.LogWarning("Purge of {UserId} refused: purge never runs in production", userId);
            return new OnboardingResult(false, PurgeRefusedInProduction);
        }

        if (await userService.GetRawUserInfoAsync(userId, ct) is null)
            return new OnboardingResult(false, "NotFound");

        if (actorId == userId)
        {
            logger.LogWarning("Purge of {UserId} refused: an admin may not purge their own account", userId);
            return new OnboardingResult(false, PurgeRefusedSelf);
        }

        // The same fan-out as the expiry path, so a purge erases no less. The Account contributor
        // owns the identity collapse; a second pass here would capture the tombstone instead of
        // the real name and break IsGdprAnonymized.
        await EraseEverySectionAsync(userId, ct);

        // Contributors run against the inner UserService, so the decorator hasn't seen the collapse.
        await userInfoRefresher.RefreshAsync(userId, ct);

        // Must not name the human: the audit log survives erasure.
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

    public async Task<AnonymizedAccountSummary?> AnonymizeExpiredAccountAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Captured before any write: the caller still needs it if the cascade throws.
        var user = await userService.GetRawUserInfoAsync(userId, ct);
        if (user is null)
            return null;

        var summary = new AnonymizedAccountSummary(
            user.Email, user.BurnerName, user.PreferredLanguage);

        // Deletion markers stay set until the last contributor succeeds, so a
        // mid-cascade failure retries the whole fan-out tomorrow.
        await EraseEverySectionAsync(userId, ct);

        // Contributors write through the inner UserService, so the decorator hasn't seen these
        // writes and admin search would keep matching the erased human by their real name.
        await userInfoRefresher.RefreshAsync(userId, ct);

        return summary;
    }

    /// <summary>
    /// Erases the account and every account merged into it, sources first and the survivor last.
    /// Sections without <see cref="IUserMerge"/> leave rows on the archived ids, and to the human
    /// it was always one account.
    /// </summary>
    /// <remarks>
    /// Cache invalidation is interleaved, not batched: a contributor that throws mid-chain still
    /// leaves every earlier id erased and dropped, and a <see cref="PurgeAsync"/> has no daily retry.
    /// </remarks>
    private async Task EraseEverySectionAsync(Guid userId, CancellationToken ct)
    {
        foreach (var subjectId in await MergeChainAsync(userId, ct))
        {
            await gdprService.EraseForUserAsync(subjectId, ct);

            // Callers only invalidate the survivor; a stale entry would keep the tombstone's name
            // searchable after it was erased.
            if (subjectId != userId)
                await userInfoRefresher.RefreshAsync(subjectId, ct);
        }
    }

    /// <summary>
    /// Archived ids folded into <paramref name="userId"/>, survivor last — the order is the contract.
    /// Read raw: erasing a tombstone id directly must erase that row, not jump to the survivor.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> MergeChainAsync(Guid userId, CancellationToken ct)
    {
        var info = await userService.GetRawUserInfoAsync(userId, ct);
        return info is null ? [userId] : [.. info.MergedUserIds, userId];
    }
}
