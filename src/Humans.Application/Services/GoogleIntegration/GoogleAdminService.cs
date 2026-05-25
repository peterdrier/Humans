using Humans.Application.DTOs;
using Humans.Application.Helpers;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Application-layer service for Google Workspace admin operations:
/// workspace account management, group linking, email backfill, account
/// linking. Migrated to the §15 pattern as part of issue #554 — no DbContext
/// dependency; all cross-section data access routes through the owning
/// services (<see cref="IUserService"/>, <see cref="IUserEmailService"/>,
/// <see cref="ITeamService"/>, <see cref="ITeamResourceService"/>).
/// </summary>
public sealed class GoogleAdminService(
    IGoogleWorkspaceUserService workspaceUserService,
    IGoogleSyncService googleSyncService,
    ITeamService teamService,
    ITeamResourceService teamResourceService,
    IUserService userService,
    IUserEmailService userEmailService,
    IAuditLogService auditLogService,
    ILogger<GoogleAdminService> logger) : IGoogleAdminService
{
    private const string NobodiesTeamDomain = "nobodies.team";

    // Resolve a single Workspace email to the linked human's UserId for audit
    // attribution. Returns null when the address isn't linked. Mirrors the
    // verified > UpdatedAt > UserId tie-break used by GetWorkspaceAccountListAsync
    // so the audit subject matches what /Google/Accounts shows.
    private async Task<Guid?> TryFindLinkedUserIdAsync(string email, CancellationToken ct)
    {
        var matches = await userEmailService.MatchByEmailsAsync([email], ct);
        return matches
            .OrderByDescending(m => m.IsVerified)
            .ThenByDescending(m => m.UpdatedAt)
            .ThenBy(m => m.UserId)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefault();
    }

    public async Task<WorkspaceAccountListResult> GetWorkspaceAccountListAsync(
        CancellationToken ct = default)
    {
        try
        {
            var accounts = await workspaceUserService.ListAccountsAsync(ct);

            // Match only against emails that correspond to Google-side accounts
            // we just listed — much cheaper than loading the entire user_emails table.
            var accountEmails = accounts.Select(a => a.PrimaryEmail).ToList();
            var matches = await userEmailService.MatchByEmailsAsync(accountEmails, ct);

            // user_emails may have verified+unverified rows per address. Pick one winner: verified > most-recent > stable UserId.
            var matchByEmail = matches
                .GroupBy(m => m.Email, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .OrderByDescending(m => m.IsVerified)
                        .ThenByDescending(m => m.UpdatedAt)
                        .ThenBy(m => m.UserId)
                        .First(),
                    StringComparer.OrdinalIgnoreCase);

            // Batch-load users for matched emails
            var matchedUserIds = matchByEmail.Values.Select(m => m.UserId).Distinct().ToList();
            var usersById = matchedUserIds.Count == 0
                ? new Dictionary<Guid, UserInfo>()
                : (await userService.GetUserInfosAsync(matchedUserIds, ct))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            var accountInfos = new List<WorkspaceAccountInfo>();
            var notPrimaryCount = 0;

            foreach (var account in accounts)
            {
                matchByEmail.TryGetValue(account.PrimaryEmail, out var matched);
                var isUsedAsPrimary = matched is { IsPrimary: true };

                // Count accounts that exist in the system but are not used as primary
                if (matched is not null && !isUsedAsPrimary)
                {
                    notPrimaryCount++;
                }

                var matchedUser = matched is not null
                    ? usersById.GetValueOrDefault(matched.UserId)
                    : null;

                accountInfos.Add(new WorkspaceAccountInfo(
                    PrimaryEmail: account.PrimaryEmail,
                    FirstName: account.FirstName,
                    LastName: account.LastName,
                    IsSuspended: account.IsSuspended,
                    CreationTime: account.CreationTime,
                    LastLoginTime: account.LastLoginTime,
                    MatchedUserId: matched?.UserId,
                    MatchedDisplayName: matchedUser?.BurnerName,
                    IsUsedAsPrimary: isUsedAsPrimary,
                    IsEnrolledIn2Sv: account.IsEnrolledIn2Sv,
                    RecoveryEmail: account.RecoveryEmail));
            }

            var sorted = accountInfos
                .OrderBy(a => a.PrimaryEmail, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var linkedCount = sorted.Count(a => a.MatchedUserId.HasValue);
            // Missing-2FA only applies to accounts that can actually be used — suspended
            // accounts can't sign in at all, so flagging their 2FA gap is noise.
            var missingTwoFactorCount = sorted.Count(a => !a.IsSuspended && !a.IsEnrolledIn2Sv);

            return new WorkspaceAccountListResult(
                Accounts: sorted,
                TotalAccounts: sorted.Count,
                ActiveAccounts: sorted.Count(a => !a.IsSuspended),
                SuspendedAccounts: sorted.Count(a => a.IsSuspended),
                LinkedAccounts: linkedCount,
                UnlinkedAccounts: sorted.Count - linkedCount,
                NotPrimaryCount: notPrimaryCount,
                MissingTwoFactorCount: missingTwoFactorCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load @nobodies.team accounts");
            return new WorkspaceAccountListResult(
                Accounts: [],
                TotalAccounts: 0,
                ActiveAccounts: 0,
                SuspendedAccounts: 0,
                LinkedAccounts: 0,
                UnlinkedAccounts: 0,
                NotPrimaryCount: 0,
                MissingTwoFactorCount: 0,
                ErrorMessage: "Failed to load @nobodies.team accounts. Check the logs for details.");
        }
    }

    public async Task<WorkspaceAccountActionResult> ProvisionStandaloneAccountAsync(
        string emailPrefix, string firstName, string lastName,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(emailPrefix) ||
            string.IsNullOrWhiteSpace(firstName) ||
            string.IsNullOrWhiteSpace(lastName))
        {
            return new WorkspaceAccountActionResult(false, ErrorMessage: "All fields are required.");
        }

        var normalizedPrefix = emailPrefix.Trim().ToLowerInvariant();
        var fullEmail = $"{normalizedPrefix}@{NobodiesTeamDomain}";

        // Check DB first: reject if the address is already tied to any human in our system.
        // Must run BEFORE the Workspace existence check so a stale/deleted Workspace account
        // cannot silently "move" the identity off its current human.
        var emailInUse = await userEmailService.IsEmailLinkedToAnyUserAsync(fullEmail, ct);
        if (emailInUse)
        {
            logger.LogWarning(
                "Standalone provisioning rejected: {Email} is already linked to a human",
                fullEmail);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"{fullEmail} is already in use by another human.");
        }

        // Belt-and-suspenders: GetByEmailOrAlternateAsync also falls back to
        // the legacy User.GoogleEmail shadow column, catching the ~200
        // pre-issue-687 users whose IsGoogle is unset on every row but the
        // legacy column still holds the address.
        var existingUser = await userService.GetByEmailOrAlternateAsync(fullEmail, ct);
        if (existingUser is not null)
        {
            logger.LogWarning(
                "Standalone provisioning rejected: {Email} is already linked to a human",
                fullEmail);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"{fullEmail} is already in use by another human.");
        }

        // Reject if the prefix collides with a team's Google Group. Team groups live on
        // the same domain (@nobodies.team), so provisioning a user account with the same
        // address would cause mail-routing chaos and break group membership.
        var conflictingTeamName = (await teamService.GetTeamsAsync(ct)).Values
            .FirstOrDefault(t => string.Equals(t.GoogleGroupPrefix, normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            ?.Name;
        if (!string.IsNullOrEmpty(conflictingTeamName))
        {
            logger.LogWarning(
                "Standalone provisioning rejected: {Email} is the Google Group address for team '{TeamName}'",
                fullEmail, conflictingTeamName);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"{fullEmail} is the Google Group address for team \"{conflictingTeamName}\" and cannot be used as a personal account.");
        }

        // Check if account already exists
        var existing = await workspaceUserService.GetAccountAsync(fullEmail, ct);
        if (existing is not null)
        {
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Account {fullEmail} already exists in Google Workspace.");
        }

        try
        {
            var tempPassword = PasswordGenerator.GenerateTemporary();

            await workspaceUserService.ProvisionAccountAsync(
                fullEmail, firstName.Trim(), lastName.Trim(), tempPassword, ct: ct);

            // Audit AFTER Google API success — business "save" here is the Workspace-side
            // provision. No local DB write happens in this flow.
            await auditLogService.LogAsync(
                AuditAction.WorkspaceAccountProvisioned,
                "WorkspaceAccount", Guid.Empty,
                $"Provisioned @{NobodiesTeamDomain} account: {fullEmail}",
                actorUserId);

            return new WorkspaceAccountActionResult(true,
                Message: $"Account {fullEmail} provisioned. Temporary password: {tempPassword}",
                TemporaryPassword: tempPassword);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision @nobodies.team account: {Email}", fullEmail);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Failed to provision {fullEmail}. Check logs for details.");
        }
    }

    public async Task<WorkspaceAccountActionResult> SuspendAccountAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await workspaceUserService.SuspendAccountAsync(email, ct);

            // Audit AFTER Google API success — the "business save" here is the
            // Workspace-side suspend. No local DB write happens in this flow.
            await auditLogService.LogAsync(
                AuditAction.WorkspaceAccountSuspended,
                "WorkspaceAccount", Guid.Empty,
                $"Suspended @{NobodiesTeamDomain} account: {email}",
                actorUserId);

            return new WorkspaceAccountActionResult(true,
                Message: $"Account {email} suspended.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to suspend account: {Email}", email);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Failed to suspend {email}.");
        }
    }

    public async Task<WorkspaceAccountActionResult> ReactivateAccountAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            await workspaceUserService.ReactivateAccountAsync(email, ct);

            // Audit AFTER Google API success — the "business save" here is the
            // Workspace-side reactivate. No local DB write happens in this flow.
            await auditLogService.LogAsync(
                AuditAction.WorkspaceAccountReactivated,
                "WorkspaceAccount", Guid.Empty,
                $"Reactivated @{NobodiesTeamDomain} account: {email}",
                actorUserId);

            return new WorkspaceAccountActionResult(true,
                Message: $"Account {email} reactivated.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reactivate account: {Email}", email);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Failed to reactivate {email}.");
        }
    }

    public async Task<WorkspaceAccountActionResult> ResetPasswordAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return new WorkspaceAccountActionResult(false, ErrorMessage: "Email is required.");
        }

        string newPassword;
        try
        {
            newPassword = PasswordGenerator.GenerateTemporary();
            await workspaceUserService.ResetPasswordAsync(email, newPassword, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset password for: {Email}", email);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Failed to reset password for {email}.");
        }

        // Audit AFTER Workspace success. Caller MUST get the temp password even if audit fails — retry would re-rotate and lock the human out.
        try
        {
            var linkedUserId = await TryFindLinkedUserIdAsync(email, ct);
            await auditLogService.LogAsync(
                AuditAction.WorkspaceAccountPasswordReset,
                "WorkspaceAccount", linkedUserId ?? Guid.Empty,
                email,
                actorUserId);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER password reset for {Email} by actor {ActorUserId}. Password was rotated; reconcile audit trail manually.",
                email, actorUserId);
        }

        return new WorkspaceAccountActionResult(true,
            Message: $"Password reset for {email}. New temporary password: {newPassword}",
            TemporaryPassword: newPassword);
    }

    private async Task<WorkspaceBackupCodesResult> GenerateBackupCodesAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default)
    {
        IReadOnlyList<string> codes;
        try
        {
            codes = await workspaceUserService.GenerateBackupCodesAsync(email, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate backup codes for: {Email}", email);
            return new WorkspaceBackupCodesResult(
                Success: false,
                Email: email,
                ErrorMessage: $"Failed to generate backup codes for {email}. Check logs for details.");
        }

        if (codes.Count == 0)
        {
            // Generate succeeded but List returned 0 — API hiccup or race.
            // No codes to deliver and nothing to audit; surface a clear failure
            // instead of recording "Generated 0 backup codes" in the audit log.
            logger.LogWarning(
                "Backup-code generation for {Email} returned 0 codes; nothing to deliver",
                email);
            return new WorkspaceBackupCodesResult(
                Success: false,
                Email: email,
                ErrorMessage: $"Backup codes were generated for {email} but none were returned. Check logs and try again.");
        }

        // Audit AFTER the Workspace-side rotation succeeds. If audit persistence
        // throws we must still hand the codes back: Google has already
        // invalidated any previously-issued set, so dropping the new ones
        // locks the human out. Surface the audit failure loudly via a critical
        // log so it can be reconciled out-of-band.
        try
        {
            // EntityId carries the linked human's UserId when the address resolves
            // to one, so the audit row renders with the human's name as subject;
            // unlinked accounts log with Guid.Empty and fall back to the email tail.
            var linkedUserId = await TryFindLinkedUserIdAsync(email, ct);
            await auditLogService.LogAsync(
                AuditAction.WorkspaceAccountBackupCodesGenerated,
                "WorkspaceAccount", linkedUserId ?? Guid.Empty,
                $"{codes.Count} code(s), {email}",
                actorUserId);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER backup codes were generated for {Email} by actor {ActorUserId}. Codes were delivered; reconcile audit trail manually.",
                email, actorUserId);
        }

        return new WorkspaceBackupCodesResult(
            Success: true,
            Email: email,
            Codes: codes,
            Message: $"Generated {codes.Count} backup code(s) for {email}. Deliver them securely — they cannot be retrieved again.");
    }

    public async Task<WorkspaceRecoveryCredentialsResult> ResetPasswordAndGenerate2FaAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return new WorkspaceRecoveryCredentialsResult(
                Success: false,
                Email: email,
                ErrorMessage: "Email is required.");
        }

        // Refuse if the account is already enrolled in 2-Step Verification.
        // The combined flow rotates the backup-code set destructively, so
        // running it against a properly-set-up account would silently break
        // the human's working 2FA. Always re-check live Directory state — the
        // UI hides the button, but a hand-crafted POST must hit the same gate.
        WorkspaceUserAccount? liveAccount;
        try
        {
            liveAccount = await workspaceUserService.GetAccountAsync(email, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch live 2SV state for: {Email}", email);
            return new WorkspaceRecoveryCredentialsResult(
                Success: false,
                Email: email,
                ErrorMessage: $"Failed to verify 2FA enrollment for {email}. Aborted; no changes made.");
        }

        if (liveAccount is null)
        {
            logger.LogWarning(
                "Reset+2FA refused for {Email}: account not found in Workspace Directory. Actor: {ActorUserId}.",
                email, actorUserId);
            return new WorkspaceRecoveryCredentialsResult(
                Success: false,
                Email: email,
                ErrorMessage: $"Account {email} not found in Workspace Directory.");
        }

        if (liveAccount.IsEnrolledIn2Sv)
        {
            logger.LogWarning(
                "Reset+2FA refused for {Email}: already enrolled in 2-Step Verification. Actor: {ActorUserId}.",
                email, actorUserId);

            // Audit AFTER the refusal decision. If audit persistence throws,
            // log Critical and still return the user-facing refusal — degrading
            // gracefully on an audit-side outage matches the BackupCodesGenerated
            // pattern below, and forcing a 500 here would mask a safe outcome
            // (no Workspace mutation happened).
            try
            {
                await auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountResetBlockedFor2Sv,
                    "WorkspaceAccount", Guid.Empty,
                    $"Refused Reset+2FA for @{NobodiesTeamDomain} account {email}: already enrolled in 2-Step Verification",
                    actorUserId);
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex,
                    "Audit-log write failed for Reset+2FA refusal on {Email} by actor {ActorUserId}. Refusal was still surfaced to the admin; reconcile audit trail manually.",
                    email, actorUserId);
            }

            return new WorkspaceRecoveryCredentialsResult(
                Success: false,
                Email: email,
                ErrorMessage: $"{email} is already enrolled in 2FA. Reset+2FA is for locked-out humans only — use the plain Reset Password button, or rotate 2FA in the Google Admin console.");
        }

        // Step 1: reset the password. Audit + return on failure — there's
        // no point grabbing a backup code if the human can't sign in anyway.
        var resetResult = await ResetPasswordAsync(email, actorUserId, ct);
        if (!resetResult.Success || resetResult.TemporaryPassword is null)
        {
            return new WorkspaceRecoveryCredentialsResult(
                Success: false,
                Email: email,
                ErrorMessage: resetResult.ErrorMessage
                    ?? $"Failed to reset password for {email}. Check logs for details.");
        }

        // Step 2: grab a backup code. If this fails, the password is still
        // valid — return Success with just the password and an explanatory
        // message so the admin can deliver what we have. (Google rotates
        // codes destructively, so a partial success is better than re-asking
        // the admin to start over.)
        var codesResult = await GenerateBackupCodesAsync(email, actorUserId, ct);
        if (!codesResult.Success || codesResult.Codes is not { Count: > 0 })
        {
            return new WorkspaceRecoveryCredentialsResult(
                Success: true,
                Email: email,
                TempPassword: resetResult.TemporaryPassword,
                BackupCode: null,
                Message: $"Password reset for {email}. Backup-code generation failed — "
                       + (codesResult.ErrorMessage
                            ?? "deliver the password only and ask the human to re-attempt the 2FA request out-of-band."));
        }

        return new WorkspaceRecoveryCredentialsResult(
            Success: true,
            Email: email,
            TempPassword: resetResult.TemporaryPassword,
            BackupCode: codesResult.Codes[0],
            Message: $"Password reset and one backup code issued for {email}. Deliver both securely — neither can be retrieved again.");
    }

    public async Task<WorkspaceAccountActionResult> LinkAccountAsync(
        string email, Guid userId,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            var user = await userService.GetUserInfoAsync(userId, ct);
            if (user is null)
            {
                return new WorkspaceAccountActionResult(false,
                    ErrorMessage: "Human not found.");
            }

            // Check not already linked (case-insensitive, any user)
            var alreadyLinked = await userEmailService.IsEmailLinkedToAnyUserAsync(email, ct);
            if (alreadyLinked)
            {
                return new WorkspaceAccountActionResult(false,
                    ErrorMessage: $"{email} is already linked to a human.");
            }

            // Add verified email; orchestrator stamps IsGoogle. Reset GoogleEmailStatus so reconciliation resumes (#687).
            await userEmailService.AddVerifiedEmailAsync(userId, email, ct);
            await userService.TrySetGoogleEmailStatusFromSyncAsync(
                userId, GoogleEmailStatus.Unknown, ct);

            // Enqueue re-sync for team memberships (outbox writes owned by Teams service).
            await teamService.EnqueueGoogleResyncForUserTeamsAsync(userId, ct);

            // Audit AFTER all business writes.
            await auditLogService.LogAsync(
                AuditAction.WorkspaceAccountLinked,
                "WorkspaceAccount", userId,
                $"Linked @{NobodiesTeamDomain} account {email}",
                actorUserId);

            return new WorkspaceAccountActionResult(true,
                Message: $"Linked {email} to {user.BurnerName}.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to link {Email} to user {UserId}", email, userId);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Failed to link {email}.");
        }
    }

    public async Task<GroupLinkActionResult> LinkGroupToTeamAsync(
        Guid teamId, string groupPrefix,
        CancellationToken ct = default)
    {
        var normalizedPrefix = groupPrefix.Trim().ToLowerInvariant();

        (bool updated, string? previousPrefix) setResult;
        try
        {
            setResult = await teamService.SetGoogleGroupPrefixAsync(
                teamId, normalizedPrefix, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to link group {GroupPrefix} to team {TeamId}", groupPrefix, teamId);
            return new GroupLinkActionResult(false,
                ErrorMessage: $"Failed to link group: {ex.Message}");
        }

        if (!setResult.updated)
        {
            return new GroupLinkActionResult(false, ErrorMessage: "Team not found.");
        }

        // Track whether the prefix write is "committed" (the Google side either
        // confirmed the group or deferred to a RequiresConfirmation warning the
        // admin has been told about). If we exit without committing — including
        // via a thrown exception from EnsureTeamGroupAsync — the finally block
        // rolls the prefix back so the DB doesn't claim a mapping that Google
        // never actually got.
        var committed = false;
        try
        {
            var team = await teamService.GetTeamByIdAsync(teamId, ct);
            var teamName = team?.Name ?? teamId.ToString();

            var linkResult = await googleSyncService.EnsureTeamGroupAsync(teamId, cancellationToken: ct);
            if (linkResult.RequiresConfirmation)
            {
                committed = true;
                return new GroupLinkActionResult(true,
                    InfoMessage: $"Linked group for team \"{teamName}\". Note: {linkResult.WarningMessage}");
            }

            if (linkResult.ErrorMessage is not null)
            {
                return new GroupLinkActionResult(false,
                    ErrorMessage: $"Could not link group: {linkResult.ErrorMessage}");
            }

            committed = true;
            return new GroupLinkActionResult(true,
                Message: $"Successfully linked {normalizedPrefix}@nobodies.team to team \"{teamName}\".");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to link group {GroupPrefix} to team {TeamId}", groupPrefix, teamId);
            return new GroupLinkActionResult(false,
                ErrorMessage: $"Failed to link group: {ex.Message}");
        }
        finally
        {
            if (!committed)
            {
                try
                {
                    await teamService.SetGoogleGroupPrefixAsync(teamId, setResult.previousPrefix, ct);
                }
                catch (Exception revertEx)
                {
                    logger.LogError(revertEx,
                        "Failed to revert GoogleGroupPrefix for team {TeamId} after failed link (prefix remains {Prefix})",
                        teamId, normalizedPrefix);
                }
            }
        }
    }

    public async Task<IReadOnlyList<TeamSummary>> GetActiveTeamsAsync(
        CancellationToken ct = default)
    {
        var options = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive);
        return options
            .Select(o => new TeamSummary(o.Id, o.Name))
            .ToList();
    }

    public async Task<EmailRenameDetectionResult> DetectEmailRenamesAsync(
        CancellationToken ct = default)
    {
        try
        {
            // Issue #635 (§15i): read UserEmails through the owning section
            // service (design-rules §2c) instead of traversing user.UserEmails
            // cross-domain. The service returns row snapshots so this caller
            // can read per-row IsVerified / IsGoogle / Provider flags.
            var allUsers = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
            var allUserIds = allUsers.Select(u => u.Id).ToList();
            var emailsByUserId = await userEmailService.GetEntitiesByUserIdsAsync(allUserIds, ct);

            var nobodiesUsers = allUsers
                .Select(u =>
                {
                    var emails = emailsByUserId.TryGetValue(u.Id, out var list)
                        ? list
                        : [];
                    var googleEmail = emails
                        .Where(e => e.IsVerified && e.IsGoogle)
                        .Select(e => e.Email)
                        .FirstOrDefault()
                        ?? emails
                            .Where(e => e.IsVerified && e.Provider != null)
                            .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                            .Select(e => e.Email)
                            .FirstOrDefault();
                    return new { u.Id, DisplayName = u.BurnerName, GoogleEmail = googleEmail };
                })
                .Where(x => x.GoogleEmail is not null &&
                    x.GoogleEmail.EndsWith($"@{NobodiesTeamDomain}", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Load resource counts per team for affected resource calculation
            var teamResourceCounts = await teamResourceService.GetActiveResourceCountsByTeamAsync(ct);

            // Active team memberships per user — delegated to ITeamService so we
            // never read team_members directly from here.
            var userTeamNameLookup = new Dictionary<Guid, IReadOnlyList<Guid>>();
            foreach (var u in nobodiesUsers)
            {
                var memberships = await teamService.GetUserTeamsAsync(u.Id, ct);
                userTeamNameLookup[u.Id] = memberships
                    .Where(m => m.LeftAt == null)
                    .Select(m => m.TeamId)
                    .ToList();
            }

            var renames = new List<EmailRenameInfo>();

            foreach (var user in nobodiesUsers)
            {
                try
                {
                    var account = await workspaceUserService.GetAccountAsync(user.GoogleEmail!, ct);

                    if (account is null)
                    {
                        // Account not found — skip (could be deleted or suspended)
                        continue;
                    }

                    if (!string.Equals(account.PrimaryEmail, user.GoogleEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        // Email was renamed
                        var affectedResources = 0;
                        if (userTeamNameLookup.TryGetValue(user.Id, out var teamIds))
                        {
                            affectedResources = teamIds.Sum(tid =>
                                teamResourceCounts.TryGetValue(tid, out var count) ? count : 0);
                        }

                        renames.Add(new EmailRenameInfo
                        {
                            UserId = user.Id,
                            DisplayName = user.DisplayName,
                            OldEmail = user.GoogleEmail!,
                            NewEmail = account.PrimaryEmail,
                            AffectedResourceCount = affectedResources
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to check email rename for user {UserId} ({Email})",
                        user.Id, user.GoogleEmail);
                }
            }

            return new EmailRenameDetectionResult
            {
                Renames = renames,
                TotalUsersChecked = nobodiesUsers.Count
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to detect email renames");
            return new EmailRenameDetectionResult
            {
                ErrorMessage = "Failed to detect email renames. Check the logs for details."
            };
        }
    }

}
