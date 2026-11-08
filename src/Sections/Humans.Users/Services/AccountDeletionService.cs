using System.Transactions;
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
using Humans.Users;

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

        int endedMemberships;
        int endedRoles;
        try
        {
            // A partial revocation must not leave an AlreadyPending request that cannot retry.
            using var scope = new TransactionScope(TransactionScopeOption.Required,
                new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                TransactionScopeAsyncFlowOption.Enabled);
            if (!await userService.SetDeletionPendingAsync(userId, now, deletionDate, eligibleAfter, ct))
                return new DeletionRequestResult(false, "NotFound");

            // Access ends during the grace period.
            endedMemberships = await teamService.RevokeAllMembershipsAsync(userId, ct);
            endedRoles = await roleAssignmentService.RevokeAllActiveAsync(userId, ct);

            scope.Complete();
        }
        catch
        {
            // The decorator may have published the pending state inside the rolled-back transaction.
            // Refresh after disposal; its failure path evicts the stale entry for a later reload.
            try
            {
                await userInfoRefresher.RefreshAsync(userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to refresh user {UserId} after deletion-request rollback", userId);
            }
            throw;
        }

        await auditLogService.LogAsync(
            UsersAuditActions.MembershipsRevokedOnDeletionRequest, nameof(User), userId,
            $"Revoked {endedMemberships} team membership(s) and {endedRoles} role assignment(s) on deletion request",
            userId);

        logger.LogWarning(
            "User {UserId} requested account deletion. Scheduled for {DeletionDate} (eligibleAfter {EligibleAfter}). " +
            "Revoked {MembershipCount} memberships and {RoleCount} roles immediately",
            userId, deletionDate, eligibleAfter, endedMemberships, endedRoles);

        // The request and access revocations are committed; confirmation cannot undo them.
        try
        {
            var notificationEmails = await userEmailService.GetNotificationTargetEmailsAsync([userId], CancellationToken.None);
            var notificationEmail = notificationEmails.GetValueOrDefault(userId) ?? user.Email;
            if (!string.IsNullOrWhiteSpace(notificationEmail))
            {
                await emailService.SendAsync(emailMessages.AccountDeletionRequested(
                    userId,
                    notificationEmail,
                    user.BurnerName,
                    effectiveDeletionDate,
                    user.PreferredLanguage.IsSupportedCultureCode()
                        ? user.PreferredLanguage : CultureCatalog.DefaultCultureCode),
                    CancellationToken.None);
            }
            else
            {
                logger.LogWarning("Skipping account-deletion request confirmation for user {UserId}: no notification email", userId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Account deletion requested for user {UserId} but its confirmation email failed", userId);
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

        // Must not name the human: the audit log survives erasure.
        const string description = "Admin-initiated purge: identity collapsed";
        if (actorId is Guid actor)
        {
            await auditLogService.LogAsync(
                UsersAuditActions.AccountPurged, nameof(User), userId, description, actor);
        }
        else
        {
            await auditLogService.LogAsync(
                UsersAuditActions.AccountPurged, nameof(User), userId, description,
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

        return summary;
    }

    /// <summary>
    /// Erases the account and every account merged into it, sources first and the survivor last.
    /// Sections without <see cref="IUserMerge"/> leave rows on the archived ids, and to the human
    /// it was always one account.
    /// </summary>
    /// <remarks>
    /// Cache invalidation is interleaved, not batched: a contributor that throws mid-chain still
    /// leaves every attempted id refreshed or evicted, and a <see cref="PurgeAsync"/> has no daily retry.
    /// </remarks>
    private async Task EraseEverySectionAsync(Guid userId, CancellationToken ct)
    {
        foreach (var subjectId in await MergeChainAsync(userId, ct))
        {
            var erased = false;
            try
            {
                await gdprService.EraseForUserAsync(subjectId, ct);
                erased = true;
            }
            finally
            {
                try
                {
                    // Contributors can commit profile erasure before a later step fails. Refresh the
                    // attempted id too, including the survivor, without the caller's cancellation.
                    await userInfoRefresher.RefreshAsync(subjectId, CancellationToken.None);
                }
                catch (Exception ex) when (!erased)
                {
                    // ReplaceAsync evicts on failure; preserve the erasure error that drives the retry.
                    logger.LogError(ex, "UserInfo refresh failed after incomplete erasure for {UserId}", subjectId);
                }
            }
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
