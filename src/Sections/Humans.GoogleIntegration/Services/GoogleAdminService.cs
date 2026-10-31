using Humans.GoogleIntegration.Contracts;
using Humans.Base.Helpers;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;

namespace Humans.GoogleIntegration.Services;

internal sealed class GoogleAdminService(
    IGoogleWorkspaceUserService workspaceUserService,
    IGoogleSyncManagement googleSyncService,
    ITeamService teamService,
    ITeamWrite teamWrite,
    ITeamResourceManagement teamResourceService,
    IUserService userService,
    IGoogleIntegrationService workspaceIdentities,
    IUserEmailService userEmailService,
    IAuditLogService auditLogService,
    ILogger<GoogleAdminService> logger) : IGoogleAdminService
{
    private const string NobodiesTeamDomain = "nobodies.team";

    // Null when the address isn't linked. Same verified > UpdatedAt > UserId tie-break as
    // GetWorkspaceAccountListAsync, so the audit subject matches /Google/Accounts.
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

            var accountEmails = accounts.Select(a => a.PrimaryEmail).ToList();
            var matches = await userEmailService.MatchByEmailsAsync(accountEmails, ct);

            // One winner per address: verified > most recent > stable UserId. Keyed by Gmail-alias identity, so a
            // row stored as an alias still resolves under Google's form.
            var matchByEmail = UserEmailMatchOwner.ByEmail(matches);

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

            var linkedCount = accountInfos.Count(a => a.MatchedUserId.HasValue);
            // Suspended accounts cannot sign in, so their missing 2FA is noise.
            var missingTwoFactorCount = accountInfos.Count(a => !a.IsSuspended && !a.IsEnrolledIn2Sv);

            return new WorkspaceAccountListResult(
                Accounts: accountInfos,
                TotalAccounts: accountInfos.Count,
                ActiveAccounts: accountInfos.Count(a => !a.IsSuspended),
                SuspendedAccounts: accountInfos.Count(a => a.IsSuspended),
                LinkedAccounts: linkedCount,
                UnlinkedAccounts: accountInfos.Count - linkedCount,
                NotPrimaryCount: notPrimaryCount,
                MissingTwoFactorCount: missingTwoFactorCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Loading @nobodies.team accounts cancelled by caller");
            throw;
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
        if (!EmailProvisioningService.IsValidWorkspaceUsername(normalizedPrefix))
        {
            logger.LogWarning("Standalone Workspace provisioning rejected an invalid username for actor {ActorUserId}", actorUserId);
            return new WorkspaceAccountActionResult(false, ErrorMessage: EmailProvisioningService.InvalidEmailPrefixMessage);
        }
        var fullEmail = $"{normalizedPrefix}@{NobodiesTeamDomain}";

        // DB check before the Workspace check, so a stale Workspace account cannot move the identity off its human.
        var emailInUse = (await userEmailService.FindByAddressAsync(fullEmail, aliased: false, verifiedOnly: false, ct)).Count > 0;
        if (emailInUse)
        {
            logger.LogWarning(
                "Standalone provisioning rejected: {Email} is already linked to a human",
                fullEmail);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"{fullEmail} is already in use by another human.");
        }

        // Not redundant: FindByAddressAsync omits tombstones, while this scans the whole warmed snapshot, so it
        // catches an address a merge or deletion tombstone owns.
        var existingUser = await userService.GetByEmailOrAlternateAsync(fullEmail, ct);
        if (existingUser is not null)
        {
            logger.LogWarning(
                "Standalone provisioning rejected: {Email} is already linked to a human",
                fullEmail);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"{fullEmail} is already in use by another human.");
        }

        // A team Group with this address on the same domain would break mail routing and group membership.
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

            // Audit after Google succeeds: the Workspace call is the business save here.
            await auditLogService.LogAsync(
                GoogleIntegrationAuditActions.WorkspaceAccountProvisioned,
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
        bool omitEmailFromAudit = false,
        CancellationToken ct = default)
    {
        try
        {
            await workspaceUserService.SuspendAccountAsync(email, ct);

            // Audit after Google succeeds: the Workspace call is the business save here.
            await auditLogService.LogAsync(
                GoogleIntegrationAuditActions.WorkspaceAccountSuspended,
                "WorkspaceAccount", Guid.Empty,
                omitEmailFromAudit
                    ? $"Suspended @{NobodiesTeamDomain} account under GDPR erasure"
                    : $"Suspended @{NobodiesTeamDomain} account: {email}",
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

            // Audit after Google succeeds: the Workspace call is the business save here.
            await auditLogService.LogAsync(
                GoogleIntegrationAuditActions.WorkspaceAccountReactivated,
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
                GoogleIntegrationAuditActions.WorkspaceAccountPasswordReset,
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
            // Generated but listed none: nothing to deliver or audit, so fail clearly rather than audit zero codes.
            logger.LogWarning(
                "Backup-code generation for {Email} returned 0 codes; nothing to deliver",
                email);
            return new WorkspaceBackupCodesResult(
                Success: false,
                Email: email,
                ErrorMessage: $"Backup codes were generated for {email} but none were returned. Check logs and try again.");
        }

        // If the audit write throws, still return the codes: Google has already invalidated the old set, so dropping
        // these locks the member out. A critical log flags it for reconciliation.
        try
        {
            // Linked accounts audit under the member's id; unlinked ones under Guid.Empty, shown by email.
            var linkedUserId = await TryFindLinkedUserIdAsync(email, ct);
            await auditLogService.LogAsync(
                GoogleIntegrationAuditActions.WorkspaceAccountBackupCodesGenerated,
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

        // Refuse an account already enrolled in 2SV: the backup-code rotation would break its working 2FA. Checked
        // live, because hiding the button does not stop a hand-crafted POST.
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

            // If the audit write throws, log Critical and still refuse: no Workspace change happened.
            try
            {
                await auditLogService.LogAsync(
                    GoogleIntegrationAuditActions.WorkspaceAccountResetBlockedFor2Sv,
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

        // Step 1. No point grabbing a backup code if the member cannot sign in anyway.
        var resetResult = await ResetPasswordAsync(email, actorUserId, ct);
        if (!resetResult.Success || resetResult.TemporaryPassword is null)
        {
            return new WorkspaceRecoveryCredentialsResult(
                Success: false,
                Email: email,
                ErrorMessage: resetResult.ErrorMessage
                    ?? $"Failed to reset password for {email}. Check logs for details.");
        }

        // Step 2. On failure the password still works, so return it alone: Google rotates codes destructively.
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

            var alreadyLinked = (await userEmailService.FindByAddressAsync(email, aliased: false, verifiedOnly: false, ct)).Count > 0;
            if (alreadyLinked)
            {
                return new WorkspaceAccountActionResult(false,
                    ErrorMessage: $"{email} is already linked to a human.");
            }

            // A verified @nobodies.team address is the Google identity, with an Unknown verdict, so reconciliation resumes.
            var emailAddition = await userEmailService.AddVerifiedEmailAsync(userId, email, ct);
            if (emailAddition.ErrorKey is not null)
                throw new InvalidOperationException("Could not add the verified email address.");

            await teamWrite.EnqueueGoogleResyncForUserTeamsAsync(userId, ct);

            await auditLogService.LogAsync(
                GoogleIntegrationAuditActions.WorkspaceAccountLinked,
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
            setResult = await teamWrite.SetGoogleGroupPrefixAsync(
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

        // Committed once Google confirmed the group or deferred with a warning the admin has seen. Otherwise, even on
        // a throw from EnsureTeamGroupAsync, the finally block rolls the prefix back.
        var committed = false;
        try
        {
            var team = await teamService.GetTeamAsync(teamId, ct);
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
                    await teamWrite.SetGoogleGroupPrefixAsync(teamId, setResult.previousPrefix, ct);
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
            // The Google identity is this section's own (google_workspace_identities).
            var allUsers = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
            var allUserIds = allUsers.Select(u => u.Id).ToList();
            var identities = await workspaceIdentities.GetWorkspaceIdentitiesAsync(allUserIds, ct);

            var nobodiesUsers = allUsers
                .Select(u => new
                {
                    u.Id,
                    DisplayName = u.BurnerName,
                    GoogleEmail = identities.TryGetValue(u.Id, out var identity) ? identity.Email : null,
                })
                .Where(x => x.GoogleEmail is not null &&
                    x.GoogleEmail.EndsWith($"@{NobodiesTeamDomain}", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var teamResourceCounts = await teamResourceService.GetActiveResourceCountsByTeamAsync(ct);

            var userTeamNameLookup = new Dictionary<Guid, IReadOnlyList<Guid>>();
            foreach (var u in nobodiesUsers)
            {
                var memberships = await teamService.GetUserTeamMembershipsAsync(u.Id, ct);
                userTeamNameLookup[u.Id] = memberships
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
