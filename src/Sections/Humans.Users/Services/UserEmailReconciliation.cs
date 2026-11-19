using Humans.AuditLog.Contracts;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Google.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Helpers;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Users.Services;

internal sealed class UserEmailReconciliation(
    IUserRepository repository,
    IUserManagement userService,
    IClock clock,
    IAuditLogService auditLogService,
    IServiceProvider serviceProvider,
    ILogger logger) : IApplicationService
{
    // Lazy: Google's identity service reads IUserService.
    private IGoogleWrite GoogleIdentity => serviceProvider.GetRequiredService<IGoogleWrite>();

    public async Task<OAuthReconcileResult> ReconcileAsync(
        Guid userId,
        string provider,
        string providerKey,
        string claimEmail,
        bool claimEmailVerified,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var rows = (await repository.GetUserEmailsByUserIdForMutationAsync(userId, cancellationToken)).ToList();

        var tagged = rows.FirstOrDefault(r =>
            string.Equals(r.Provider, provider, StringComparison.Ordinal) &&
            string.Equals(r.ProviderKey, providerKey, StringComparison.Ordinal));

        // 1. NoChange
        if (tagged is not null &&
            string.Equals(tagged.Email, claimEmail, StringComparison.OrdinalIgnoreCase))
        {
            return new OAuthReconcileResult(
                ReconcileOutcome.NoChange, null, tagged.Id, null, null, null, false);
        }

        var siblingAtClaim = rows.FirstOrDefault(r =>
            !ReferenceEquals(r, tagged) &&
            string.Equals(r.Email, claimEmail, StringComparison.OrdinalIgnoreCase));

        // Cross-user check before any mutation, normalized so dot-aliases can't bypass the gate.
        var normalizedClaim = EmailNormalization.NormalizeForComparison(claimEmail);
        var alternateClaim = EmailLookupAliases.GetAlternate(normalizedClaim);
        var blocker = (await repository.GetUserEmailsByAddressAsync(
                normalizedClaim, alternateClaim, cancellationToken))
            .FirstOrDefault(e => e.IsVerified && e.UserId != userId);

        // 2. CrossUserBlocked: another user verified-holds the claim and the claim is unverified.
        if (blocker is not null && !claimEmailVerified)
        {
            var blockerRows = await repository.GetUserEmailsByUserIdForMutationAsync(
                blocker.UserId, cancellationToken);

            var blockedDescription = await BuildCrossUserDiagnosticAsync(
                kind: "BLOCKED (unverified provider claim)",
                userId, signingRows: rows,
                provider, providerKey, claimEmail, claimEmailVerified,
                previousEmail: tagged?.Email,
                displaced: blocker,
                displacedRows: blockerRows,
                displacedUserLeftWithoutVerifiedEmail: false,
                cancellationToken);

            logger.LogError(
                "OAuth cross-user collision BLOCKED (unverified claim): {Description}",
                blockedDescription);

            await auditLogService.LogAsync(
                UsersAuditActions.OAuthRenameCollisionBlocked,
                nameof(User), userId,
                blockedDescription,
                actorUserId: userId,
                relatedEntityId: blocker.Id, relatedEntityType: nameof(UserEmail));

            return new OAuthReconcileResult(
                ReconcileOutcome.CrossUserBlocked,
                PreviousEmail: tagged?.Email,
                AffectedRowId: null,
                DisplacedUserId: blocker.UserId,
                DisplacedRowId: blocker.Id,
                DisplacedEmail: blocker.Email,
                DisplacedUserLeftWithoutVerifiedEmail: false);
        }

        // 3. Decide cross-user displacement (snapshot only; commits with step 5).
        var displaced = false;
        Guid? displacedUserId = null;
        Guid? displacedRowId = null;
        string? displacedEmail = null;
        var displacedUserLeftWithoutVerifiedEmail = false;
        string? collisionDiagnostic = null;

        if (blocker is not null && claimEmailVerified)
        {
            var displacedUsersRows = await repository.GetUserEmailsByUserIdForMutationAsync(
                blocker.UserId, cancellationToken);
            displacedUserLeftWithoutVerifiedEmail = displacedUsersRows
                .Count(r => r.IsVerified && r.Id != blocker.Id) == 0;

            displaced = true;
            displacedUserId = blocker.UserId;
            displacedRowId = blocker.Id;
            displacedEmail = blocker.Email;

            collisionDiagnostic = await BuildCrossUserDiagnosticAsync(
                kind: "DISPLACED",
                userId, signingRows: rows,
                provider, providerKey, claimEmail, claimEmailVerified,
                previousEmail: tagged?.Email,
                displaced: blocker,
                displacedRows: displacedUsersRows,
                displacedUserLeftWithoutVerifiedEmail: displacedUserLeftWithoutVerifiedEmail,
                cancellationToken);
        }

        // 4. Build the signing user's mutation plan (in memory).
        ReconcileOutcome outcome;
        Guid affectedRowId;
        string? previousEmail = tagged?.Email;
        AuditActionDefinition signingAction;
        string signingDescription;

        UserEmail? rowToUpdate = null;
        UserEmail? rowToDelete = null;
        UserEmail? rowToInsert = null;

        if (tagged is not null && siblingAtClaim is not null)
        {
            // Tag-move: old-tagged row + sibling holds claim email.
            siblingAtClaim.Provider = provider;
            siblingAtClaim.ProviderKey = providerKey;
            siblingAtClaim.IsVerified = true;
            siblingAtClaim.IsPrimary = siblingAtClaim.IsPrimary || tagged.IsPrimary;
            siblingAtClaim.UpdatedAt = now;
            tagged.IsPrimary = false;

            rowToUpdate = siblingAtClaim;
            rowToDelete = tagged;

            outcome = ReconcileOutcome.TagMoved;
            affectedRowId = siblingAtClaim.Id;
            signingAction = UsersAuditActions.GoogleEmailRenamed;
            signingDescription =
                $"OAuth tag moved from `{previousEmail}` to `{siblingAtClaim.Email}` " +
                $"(sub={providerKey}); old row deleted, flags unioned onto matching row.";
        }
        else if (tagged is not null)
        {
            // Rewrite in place — no sibling at claim email.
            tagged.Email = claimEmail;
            tagged.IsVerified = true;
            tagged.UpdatedAt = now;

            rowToUpdate = tagged;

            outcome = ReconcileOutcome.EmailRewritten;
            affectedRowId = tagged.Id;
            signingAction = UsersAuditActions.GoogleEmailRenamed;
            signingDescription =
                $"OAuth email renamed `{previousEmail}` -> `{claimEmail}` " +
                $"(sub={providerKey}).";
        }
        else if (siblingAtClaim is not null)
        {
            // Attach tag to existing row (legacy backfill / first OAuth on plain-email account).
            siblingAtClaim.Provider = provider;
            siblingAtClaim.ProviderKey = providerKey;
            siblingAtClaim.IsVerified = true;
            siblingAtClaim.UpdatedAt = now;

            rowToUpdate = siblingAtClaim;

            outcome = ReconcileOutcome.TagMoved;
            affectedRowId = siblingAtClaim.Id;
            signingAction = UsersAuditActions.GoogleEmailRenamed;
            signingDescription =
                $"OAuth tag attached to existing row `{siblingAtClaim.Email}` " +
                $"(sub={providerKey}).";
        }
        else
        {
            rowToInsert = new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Email = claimEmail,
                IsVerified = true,
                IsPrimary = false,
                Provider = provider,
                ProviderKey = providerKey,
                CreatedAt = now,
                UpdatedAt = now,
            };

            outcome = ReconcileOutcome.NewRowCreated;
            affectedRowId = rowToInsert.Id;
            signingAction = UsersAuditActions.UserEmailLinked;
            signingDescription =
                $"OAuth callback: created UserEmail row " +
                $"(provider={provider}, sub={providerKey}, email={claimEmail}).";
        }

        // 5. Apply displacement + signing-user mutation atomically and repair UserEmail invariants.
        await userService.ApplyUserEmailReconcilePlanAsync(
            userId,
            new UserEmailReconcilePlanCommand(
                DisplacedRowToDelete: displaced ? blocker : null,
                RowToDelete: rowToDelete,
                RowToUpdate: rowToUpdate,
                RowToInsert: rowToInsert),
            cancellationToken);

        // Addresses that left an account: a tag-move deletes the old row, a rewrite renames it in
        // place, a displacement deletes the other user's row. Each stops being a Google identity.
        if (previousEmail is not null && outcome is ReconcileOutcome.TagMoved or ReconcileOutcome.EmailRewritten
            && !string.Equals(previousEmail, claimEmail, StringComparison.OrdinalIgnoreCase))
            await GoogleIdentity.ClearIfAddressAsync(userId, previousEmail, cancellationToken);
        if (displaced)
            await GoogleIdentity.ClearIfAddressAsync(blocker!.UserId, blocker.Email, cancellationToken);

        // 6. Audit. A displacement writes the OAuthRenameCollision pair instead.
        if (displaced)
        {
            logger.LogError(
                "OAuth cross-user displacement (verified claim): {Description}",
                collisionDiagnostic);

            await auditLogService.LogAsync(
                UsersAuditActions.OAuthRenameCollision,
                nameof(User), userId,
                collisionDiagnostic!,
                actorUserId: userId,
                relatedEntityId: blocker!.Id, relatedEntityType: nameof(UserEmail));

            var displacedAuditDescription =
                $"Row `{blocker.Email}` deleted by OAuth callback for user {userId} " +
                $"(provider={provider}, sub={providerKey})." +
                (displacedUserLeftWithoutVerifiedEmail
                    ? " Displaced user left with zero verified emails."
                    : string.Empty);
            await auditLogService.LogAsync(
                UsersAuditActions.UserEmailDisplacedByOAuthRename,
                nameof(User), blocker.UserId,
                displacedAuditDescription,
                actorUserId: userId,
                relatedEntityId: blocker.Id, relatedEntityType: nameof(UserEmail));

            return new OAuthReconcileResult(
                ReconcileOutcome.CrossUserDisplaced,
                previousEmail,
                affectedRowId,
                displacedUserId,
                displacedRowId,
                displacedEmail,
                displacedUserLeftWithoutVerifiedEmail);
        }

        await auditLogService.LogAsync(
            signingAction,
            nameof(User), userId,
            signingDescription,
            actorUserId: userId,
            relatedEntityId: affectedRowId, relatedEntityType: nameof(UserEmail));

        return new OAuthReconcileResult(
            outcome,
            previousEmail,
            affectedRowId,
            displacedUserId,
            displacedRowId,
            displacedEmail,
            displacedUserLeftWithoutVerifiedEmail);
    }

    private async Task<string> BuildCrossUserDiagnosticAsync(
        string kind,
        Guid signingUserId,
        IReadOnlyList<UserEmail> signingRows,
        string provider,
        string providerKey,
        string claimEmail,
        bool claimEmailVerified,
        string? previousEmail,
        UserEmail displaced,
        IReadOnlyList<UserEmail> displacedRows,
        bool displacedUserLeftWithoutVerifiedEmail,
        CancellationToken ct)
    {
        var loginsByUser = await userService.GetExternalLoginsByUserIdsAsync([signingUserId, displaced.UserId], ct);
        var signingLogins = loginsByUser.TryGetValue(signingUserId, out var sl) ? sl : [];
        var displacedLogins = loginsByUser.TryGetValue(displaced.UserId, out var dl) ? dl : [];

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append(inv, $"OAuth cross-user {kind}: provider={provider} sub={providerKey} ");
        sb.Append(inv, $"claimEmail={claimEmail} claimEmailVerified={claimEmailVerified} ");
        sb.Append(inv, $"previousEmail={previousEmail ?? "(none)"} attemptedAt={clock.GetCurrentInstant()}. ");
        sb.Append(inv, $"SigningUser={signingUserId} rows=[");
        sb.Append(string.Join(", ", signingRows.Select(FormatRow)));
        sb.Append("] logins=[");
        sb.Append(string.Join(", ", signingLogins.Select(l => $"{l.Provider}/{l.ProviderKey}")));
        sb.Append(inv, $"]. DisplacedUser={displaced.UserId} displacedRowId={displaced.Id} ");
        sb.Append(inv, $"displacedEmail={displaced.Email}");
        if (displacedUserLeftWithoutVerifiedEmail)
            sb.Append(" — displaced user left with zero verified emails after delete");
        sb.Append(" rows=[");
        sb.Append(string.Join(", ", displacedRows.Select(FormatRow)));
        sb.Append("] logins=[");
        sb.Append(string.Join(", ", displacedLogins.Select(l => $"{l.Provider}/{l.ProviderKey}")));
        sb.Append("].");
        return sb.ToString();

        static string FormatRow(UserEmail r) =>
            $"{{Id={r.Id} Email={r.Email} V={r.IsVerified} P={r.IsPrimary} " +
            $"Provider={r.Provider ?? "(none)"} Key={r.ProviderKey ?? "(none)"}}}";
    }
}
