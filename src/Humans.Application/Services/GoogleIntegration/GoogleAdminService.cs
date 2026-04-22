using Humans.Application.DTOs;
using Humans.Application.Helpers;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Application-layer service for Google Workspace admin operations:
/// workspace account management, group linking, email backfill, account
/// linking. Migrated to the §15 pattern as part of issue #554 — no DbContext
/// dependency; all cross-section data access routes through the owning
/// services (<see cref="IUserService"/>, <see cref="IUserEmailService"/>,
/// <see cref="ITeamService"/>, <see cref="ITeamResourceService"/>).
/// </summary>
public sealed class GoogleAdminService : IGoogleAdminService
{
    private readonly IGoogleWorkspaceUserService _workspaceUserService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly ITeamService _teamService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<GoogleAdminService> _logger;

    private const string NobodiesTeamDomain = "nobodies.team";

    public GoogleAdminService(
        IGoogleWorkspaceUserService workspaceUserService,
        IGoogleSyncService googleSyncService,
        ITeamService teamService,
        ITeamResourceService teamResourceService,
        IUserService userService,
        IUserEmailService userEmailService,
        IAuditLogService auditLogService,
        ILogger<GoogleAdminService> logger)
    {
        _workspaceUserService = workspaceUserService;
        _googleSyncService = googleSyncService;
        _teamService = teamService;
        _teamResourceService = teamResourceService;
        _userService = userService;
        _userEmailService = userEmailService;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<WorkspaceAccountListResult> GetWorkspaceAccountListAsync(
        CancellationToken ct = default)
    {
        try
        {
            var accounts = await _workspaceUserService.ListAccountsAsync(ct);

            // Match only against emails that correspond to Google-side accounts
            // we just listed — much cheaper than loading the entire user_emails table.
            var accountEmails = accounts.Select(a => a.PrimaryEmail).ToList();
            var matches = await _userEmailService.MatchByEmailsAsync(accountEmails, ct);

            // user_emails allows a verified + unverified row for the same
            // address, so MatchByEmailsAsync can return multiple hits per
            // email. Collapse to one winner per address: prefer verified
            // rows, then the most recently updated, then a stable UserId
            // tie-breaker. Using ToDictionary directly would throw on the
            // duplicate key and fail the whole workspace-accounts view.
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
                ? new Dictionary<Guid, User>()
                : (await _userService.GetByIdsAsync(matchedUserIds, ct))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            var accountInfos = new List<WorkspaceAccountInfo>();
            var notPrimaryCount = 0;

            foreach (var account in accounts)
            {
                matchByEmail.TryGetValue(account.PrimaryEmail, out var matched);
                var isUsedAsPrimary = matched is { IsNotificationTarget: true };

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
                    MatchedDisplayName: matchedUser?.DisplayName,
                    IsUsedAsPrimary: isUsedAsPrimary));
            }

            var sorted = accountInfos
                .OrderBy(a => a.PrimaryEmail, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var linkedCount = sorted.Count(a => a.MatchedUserId.HasValue);

            return new WorkspaceAccountListResult(
                Accounts: sorted,
                TotalAccounts: sorted.Count,
                ActiveAccounts: sorted.Count(a => !a.IsSuspended),
                SuspendedAccounts: sorted.Count(a => a.IsSuspended),
                LinkedAccounts: linkedCount,
                UnlinkedAccounts: sorted.Count - linkedCount,
                NotPrimaryCount: notPrimaryCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load @nobodies.team accounts");
            return new WorkspaceAccountListResult(
                Accounts: [],
                TotalAccounts: 0,
                ActiveAccounts: 0,
                SuspendedAccounts: 0,
                LinkedAccounts: 0,
                UnlinkedAccounts: 0,
                NotPrimaryCount: 0,
                ErrorMessage: "Failed to load @nobodies.team accounts. Check the logs for details.");
        }
    }

    public async Task<WorkspaceAccountActionResult> ProvisionStandaloneAccountAsync(
        string emailPrefix, string firstName, string lastName,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var normalizedPrefix = emailPrefix.Trim().ToLowerInvariant();
        var fullEmail = $"{normalizedPrefix}@{NobodiesTeamDomain}";

        // Check DB first: reject if the address is already tied to any human in our system.
        // Must run BEFORE the Workspace existence check so a stale/deleted Workspace account
        // cannot silently "move" the identity off its current human.
        var emailInUse = await _userEmailService.IsEmailLinkedToAnyUserAsync(fullEmail, ct);
        if (emailInUse)
        {
            _logger.LogWarning(
                "Standalone provisioning rejected: {Email} is already linked to a human",
                fullEmail);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"{fullEmail} is already in use by another human.");
        }

        // Also reject if the address is set as a User.GoogleEmail (or User.Email) — the
        // user_emails check covers verified/linked rows, this covers the User row itself.
        var existingUser = await _userService.GetByEmailOrAlternateAsync(fullEmail, ct);
        if (existingUser is not null)
        {
            _logger.LogWarning(
                "Standalone provisioning rejected: {Email} is already set as GoogleEmail for a human",
                fullEmail);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"{fullEmail} is already in use by another human.");
        }

        // Reject if the prefix collides with a team's Google Group. Team groups live on
        // the same domain (@nobodies.team), so provisioning a user account with the same
        // address would cause mail-routing chaos and break group membership.
        var conflictingTeamName = await _teamService.GetTeamNameByGoogleGroupPrefixAsync(
            normalizedPrefix, ct);
        if (!string.IsNullOrEmpty(conflictingTeamName))
        {
            _logger.LogWarning(
                "Standalone provisioning rejected: {Email} is the Google Group address for team '{TeamName}'",
                fullEmail, conflictingTeamName);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"{fullEmail} is the Google Group address for team \"{conflictingTeamName}\" and cannot be used as a personal account.");
        }

        // Check if account already exists
        var existing = await _workspaceUserService.GetAccountAsync(fullEmail, ct);
        if (existing is not null)
        {
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Account {fullEmail} already exists in Google Workspace.");
        }

        try
        {
            var tempPassword = PasswordGenerator.GenerateTemporary();

            await _workspaceUserService.ProvisionAccountAsync(
                fullEmail, firstName.Trim(), lastName.Trim(), tempPassword, ct: ct);

            // Audit AFTER Google API success — business "save" here is the Workspace-side
            // provision. No local DB write happens in this flow.
            await _auditLogService.LogAsync(
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
            _logger.LogError(ex, "Failed to provision @nobodies.team account: {Email}", fullEmail);
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
            await _workspaceUserService.SuspendAccountAsync(email, ct);

            // Audit AFTER Google API success — the "business save" here is the
            // Workspace-side suspend. No local DB write happens in this flow.
            await _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountSuspended,
                "WorkspaceAccount", Guid.Empty,
                $"Suspended @{NobodiesTeamDomain} account: {email}",
                actorUserId);

            return new WorkspaceAccountActionResult(true,
                Message: $"Account {email} suspended.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to suspend account: {Email}", email);
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
            await _workspaceUserService.ReactivateAccountAsync(email, ct);

            // Audit AFTER Google API success — the "business save" here is the
            // Workspace-side reactivate. No local DB write happens in this flow.
            await _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountReactivated,
                "WorkspaceAccount", Guid.Empty,
                $"Reactivated @{NobodiesTeamDomain} account: {email}",
                actorUserId);

            return new WorkspaceAccountActionResult(true,
                Message: $"Account {email} reactivated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reactivate account: {Email}", email);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Failed to reactivate {email}.");
        }
    }

    public async Task<WorkspaceAccountActionResult> ResetPasswordAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            var newPassword = PasswordGenerator.GenerateTemporary();
            await _workspaceUserService.ResetPasswordAsync(email, newPassword, ct);

            // Audit AFTER Google API success — the "business save" here is the
            // Workspace-side password reset. No local DB write happens in this flow.
            await _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountPasswordReset,
                "WorkspaceAccount", Guid.Empty,
                $"Reset password for @{NobodiesTeamDomain} account: {email}",
                actorUserId);

            return new WorkspaceAccountActionResult(true,
                Message: $"Password reset for {email}. New temporary password: {newPassword}",
                TemporaryPassword: newPassword);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset password for: {Email}", email);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Failed to reset password for {email}.");
        }
    }

    public async Task<WorkspaceAccountActionResult> LinkAccountAsync(
        string email, Guid userId,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            var user = await _userService.GetByIdAsync(userId, ct);
            if (user is null)
            {
                return new WorkspaceAccountActionResult(false,
                    ErrorMessage: "Human not found.");
            }

            // Check not already linked (case-insensitive, any user)
            var alreadyLinked = await _userEmailService.IsEmailLinkedToAnyUserAsync(email, ct);
            if (alreadyLinked)
            {
                return new WorkspaceAccountActionResult(false,
                    ErrorMessage: $"{email} is already linked to a human.");
            }

            // Add as verified email (also sets notification target for @nobodies.team).
            // Self-persists via IUserEmailRepository.
            await _userEmailService.AddVerifiedEmailAsync(userId, email, ct);

            // Auto-set as Google service email and reset sync state (also invalidates
            // FullProfile). Self-persists via IUserRepository.
            await _userService.SetGoogleEmailAsync(userId, email, ct);

            // Enqueue re-sync for all current team memberships — delegated to the
            // Teams-owning service so google_sync_outbox_events writes stay
            // colocated with the rest of the outbox flow. Self-persists.
            await _teamService.EnqueueGoogleResyncForUserTeamsAsync(userId, ct);

            // Audit AFTER every business write completes so a failing upstream call
            // never leaves a phantom "linked" audit row.
            await _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountLinked,
                "WorkspaceAccount", userId,
                $"Linked @{NobodiesTeamDomain} account {email}",
                actorUserId);

            return new WorkspaceAccountActionResult(true,
                Message: $"Linked {email} to {user.DisplayName}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to link {Email} to user {UserId}", email, userId);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Failed to link {email}.");
        }
    }

    public async Task<EmailBackfillActionResult> ApplyEmailBackfillAsync(
        List<Guid> selectedUserIds, Dictionary<string, string> corrections,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var updated = 0;
        var errors = new List<string>();

        foreach (var userId in selectedUserIds)
        {
            if (!corrections.TryGetValue(userId.ToString(), out var googleEmail) || string.IsNullOrEmpty(googleEmail))
                continue;

            try
            {
                var (wasUpdated, oldEmail) = await _userService.ApplyEmailBackfillAsync(
                    userId, googleEmail, ct);

                if (!wasUpdated)
                {
                    errors.Add($"User {userId} not found.");
                    continue;
                }

                _logger.LogInformation(
                    "Admin {AdminId} applying email backfill for user {UserId}: '{OldEmail}' -> '{NewEmail}'",
                    actorUserId, userId, oldEmail, googleEmail);

                updated++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update email for user {UserId}", userId);
                errors.Add($"Error updating user {userId}: {ex.Message}");
            }
        }

        return new EmailBackfillActionResult(updated, errors);
    }

    public async Task<GroupLinkActionResult> LinkGroupToTeamAsync(
        Guid teamId, string groupPrefix,
        CancellationToken ct = default)
    {
        var normalizedPrefix = groupPrefix.Trim().ToLowerInvariant();

        (bool updated, string? previousPrefix) setResult;
        try
        {
            setResult = await _teamService.SetGoogleGroupPrefixAsync(
                teamId, normalizedPrefix, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to link group {GroupPrefix} to team {TeamId}", groupPrefix, teamId);
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
            var team = await _teamService.GetTeamByIdAsync(teamId, ct);
            var teamName = team?.Name ?? teamId.ToString();

            var linkResult = await _googleSyncService.EnsureTeamGroupAsync(teamId, cancellationToken: ct);
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
            _logger.LogError(ex, "Failed to link group {GroupPrefix} to team {TeamId}", groupPrefix, teamId);
            return new GroupLinkActionResult(false,
                ErrorMessage: $"Failed to link group: {ex.Message}");
        }
        finally
        {
            if (!committed)
            {
                try
                {
                    await _teamService.SetGoogleGroupPrefixAsync(teamId, setResult.previousPrefix, ct);
                }
                catch (Exception revertEx)
                {
                    _logger.LogError(revertEx,
                        "Failed to revert GoogleGroupPrefix for team {TeamId} after failed link (prefix remains {Prefix})",
                        teamId, normalizedPrefix);
                }
            }
        }
    }

    public async Task<IReadOnlyList<TeamSummary>> GetActiveTeamsAsync(
        CancellationToken ct = default)
    {
        var options = await _teamService.GetActiveTeamOptionsAsync(ct);
        return options
            .Select(o => new TeamSummary(o.Id, o.Name))
            .ToList();
    }

    public async Task<EmailRenameDetectionResult> DetectEmailRenamesAsync(
        CancellationToken ct = default)
    {
        try
        {
            // Load all users, then filter to those with a @nobodies.team GoogleEmail.
            var allUsers = await _userService.GetAllUsersAsync(ct);

            var nobodiesUsers = allUsers
                .Where(u => u.GoogleEmail is not null &&
                    u.GoogleEmail.EndsWith($"@{NobodiesTeamDomain}", StringComparison.OrdinalIgnoreCase))
                .Select(u => new { u.Id, u.DisplayName, u.GoogleEmail })
                .ToList();

            // Load resource counts per team for affected resource calculation
            var teamResourceCounts = await _teamResourceService.GetActiveResourceCountsByTeamAsync(ct);

            // Active team memberships per user — delegated to ITeamService so we
            // never read team_members directly from here.
            var userTeamNameLookup = new Dictionary<Guid, IReadOnlyList<Guid>>();
            foreach (var u in nobodiesUsers)
            {
                var memberships = await _teamService.GetUserTeamsAsync(u.Id, ct);
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
                    var account = await _workspaceUserService.GetAccountAsync(user.GoogleEmail!, ct);

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
                            DisplayName = user.DisplayName ?? "Unknown",
                            OldEmail = user.GoogleEmail!,
                            NewEmail = account.PrimaryEmail,
                            AffectedResourceCount = affectedResources
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
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
            _logger.LogError(ex, "Failed to detect email renames");
            return new EmailRenameDetectionResult
            {
                ErrorMessage = "Failed to detect email renames. Check the logs for details."
            };
        }
    }

    public async Task<EmailRenameFixResult> FixEmailRenameAsync(
        Guid userId, string newEmail, Guid actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            var user = await _userService.GetByIdAsync(userId, ct);
            if (user is null)
            {
                return new EmailRenameFixResult(false, ErrorMessage: "Human not found.");
            }

            var oldEmail = user.GoogleEmail;
            if (string.IsNullOrEmpty(oldEmail))
            {
                return new EmailRenameFixResult(false,
                    ErrorMessage: "Human does not have a GoogleEmail set.");
            }

            // Update User.GoogleEmail and reset sync status so reconciliation resumes
            var updated = await _userService.SetGoogleEmailAsync(userId, newEmail, ct);
            if (!updated)
            {
                return new EmailRenameFixResult(false,
                    ErrorMessage: "Human not found during update.");
            }

            // Update the corresponding UserEmail row if one exists for the old email —
            // delegated to the owning section so no user_emails writes happen here.
            // Self-persists via IUserEmailRepository.
            await _userEmailService.RewriteEmailAddressAsync(userId, oldEmail, newEmail, ct);

            _logger.LogInformation(
                "Admin {AdminId} fixing email rename for user {UserId}: '{OldEmail}' -> '{NewEmail}'",
                actorUserId, userId, oldEmail, newEmail);

            // Audit AFTER every business write completes so a failing upstream call
            // never leaves a phantom rename audit row.
            await _auditLogService.LogAsync(
                AuditAction.GoogleEmailRenamed,
                "User", userId,
                $"Fixed email rename: {oldEmail} -> {newEmail}",
                actorUserId);

            return new EmailRenameFixResult(true,
                Message: $"Updated email for {user.DisplayName}: {oldEmail} -> {newEmail}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fix email rename for user {UserId}", userId);
            return new EmailRenameFixResult(false,
                ErrorMessage: $"Failed to fix email rename: {ex.Message}");
        }
    }
}
