using Humans.Application.Helpers;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Service for Google Workspace admin operations:
/// workspace account management, group linking, email backfill, account linking.
/// </summary>
public class GoogleAdminService : IGoogleAdminService
{
    private readonly HumansDbContext _dbContext;
    private readonly IGoogleWorkspaceUserService _workspaceUserService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IUserEmailService _userEmailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly ILogger<GoogleAdminService> _logger;

    private const string NobodiesTeamDomain = "nobodies.team";

    public GoogleAdminService(
        HumansDbContext dbContext,
        IGoogleWorkspaceUserService workspaceUserService,
        IGoogleSyncService googleSyncService,
        IUserEmailService userEmailService,
        IAuditLogService auditLogService,
        IClock clock,
        ILogger<GoogleAdminService> logger)
    {
        _dbContext = dbContext;
        _workspaceUserService = workspaceUserService;
        _googleSyncService = googleSyncService;
        _userEmailService = userEmailService;
        _auditLogService = auditLogService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<WorkspaceAccountListResult> GetWorkspaceAccountListAsync(
        CancellationToken ct = default)
    {
        try
        {
            var accounts = await _workspaceUserService.ListAccountsAsync(ct);

            // Load all user emails to match accounts to humans
            var allUserEmails = await _dbContext.UserEmails
                .AsNoTracking()
                .Include(ue => ue.User)
                .ToListAsync(ct);

            var accountInfos = new List<WorkspaceAccountInfo>();
            var notPrimaryCount = 0;

            foreach (var account in accounts)
            {
                var matchedEmail = allUserEmails.FirstOrDefault(ue =>
                    string.Equals(ue.Email, account.PrimaryEmail, StringComparison.OrdinalIgnoreCase));

                var isUsedAsPrimary = matchedEmail is { IsNotificationTarget: true };

                // Count accounts that exist in the system but are not used as primary
                if (matchedEmail is not null && !isUsedAsPrimary)
                {
                    notPrimaryCount++;
                }

                accountInfos.Add(new WorkspaceAccountInfo(
                    PrimaryEmail: account.PrimaryEmail,
                    FirstName: account.FirstName,
                    LastName: account.LastName,
                    IsSuspended: account.IsSuspended,
                    CreationTime: account.CreationTime,
                    LastLoginTime: account.LastLoginTime,
                    MatchedUserId: matchedEmail?.UserId,
                    MatchedDisplayName: matchedEmail?.User?.DisplayName,
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
        var fullEmail = $"{emailPrefix.Trim().ToLowerInvariant()}@{NobodiesTeamDomain}";

        // Check if account already exists
        var existing = await _workspaceUserService.GetAccountAsync(fullEmail, ct);
        if (existing is not null)
        {
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Account {fullEmail} already exists.");
        }

        try
        {
            var tempPassword = PasswordGenerator.GenerateTemporary();

            await _workspaceUserService.ProvisionAccountAsync(
                fullEmail, firstName.Trim(), lastName.Trim(), tempPassword, ct: ct);

            await _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountProvisioned,
                "WorkspaceAccount", Guid.Empty,
                $"Provisioned @{NobodiesTeamDomain} account: {fullEmail}",
                actorUserId);
            await _dbContext.SaveChangesAsync(ct);

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

            await _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountSuspended,
                "WorkspaceAccount", Guid.Empty,
                $"Suspended @{NobodiesTeamDomain} account: {email}",
                actorUserId);
            await _dbContext.SaveChangesAsync(ct);

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

            await _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountReactivated,
                "WorkspaceAccount", Guid.Empty,
                $"Reactivated @{NobodiesTeamDomain} account: {email}",
                actorUserId);
            await _dbContext.SaveChangesAsync(ct);

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

            await _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountPasswordReset,
                "WorkspaceAccount", Guid.Empty,
                $"Reset password for @{NobodiesTeamDomain} account: {email}",
                actorUserId);
            await _dbContext.SaveChangesAsync(ct);

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
            var user = await _dbContext.Users.FindAsync([userId], ct);
            if (user is null)
            {
                return new WorkspaceAccountActionResult(false,
                    ErrorMessage: "Human not found.");
            }

            // Check not already linked
            var alreadyLinked = await _dbContext.UserEmails
                .AnyAsync(ue => EF.Functions.ILike(ue.Email, email), ct);
            if (alreadyLinked)
            {
                return new WorkspaceAccountActionResult(false,
                    ErrorMessage: $"{email} is already linked to a human.");
            }

            // Add as verified email (also sets notification target for @nobodies.team)
            await _userEmailService.AddVerifiedEmailAsync(userId, email, ct);

            // Auto-set as Google service email and reset sync state
            user.GoogleEmail = email;
            user.GoogleEmailStatus = GoogleEmailStatus.Unknown;
            await _dbContext.SaveChangesAsync(ct);

            // Enqueue re-sync for all current team memberships
            var memberships = await _dbContext.TeamMembers
                .Where(tm => tm.UserId == userId && tm.LeftAt == null)
                .Select(tm => new { tm.Id, tm.TeamId })
                .ToListAsync(ct);

            var now = _clock.GetCurrentInstant();
            foreach (var membership in memberships)
            {
                var dedupeKey = $"{membership.Id}:{GoogleSyncOutboxEventTypes.AddUserToTeamResources}:link:{now}";
                _dbContext.GoogleSyncOutboxEvents.Add(new GoogleSyncOutboxEvent
                {
                    Id = Guid.NewGuid(),
                    EventType = GoogleSyncOutboxEventTypes.AddUserToTeamResources,
                    TeamId = membership.TeamId,
                    UserId = userId,
                    OccurredAt = now,
                    DeduplicationKey = dedupeKey
                });
            }

            if (memberships.Count > 0)
            {
                await _dbContext.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "Enqueued {Count} re-sync events for user {UserId} after admin email link",
                    memberships.Count, userId);
            }

            // Audit
            await _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountLinked,
                "WorkspaceAccount", userId,
                $"Linked @{NobodiesTeamDomain} account {email}",
                actorUserId);
            await _dbContext.SaveChangesAsync(ct);

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
                var user = await _dbContext.Users
                    .Include(u => u.UserEmails)
                    .FirstOrDefaultAsync(u => u.Id == userId, ct);

                if (user is null)
                {
                    errors.Add($"User {userId} not found.");
                    continue;
                }

                var oldEmail = user.Email;

                user.Email = googleEmail;
                user.UserName = googleEmail;
                user.NormalizedEmail = googleEmail.ToUpperInvariant();
                user.NormalizedUserName = googleEmail.ToUpperInvariant();

                // Update OAuth UserEmail record if it exists
                var oauthEmail = user.UserEmails.FirstOrDefault(e => e.IsOAuth);
                if (oauthEmail is not null)
                {
                    oauthEmail.Email = googleEmail;
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

        if (updated > 0)
            await _dbContext.SaveChangesAsync(ct);

        return new EmailBackfillActionResult(updated, errors);
    }

    public async Task<GroupLinkActionResult> LinkGroupToTeamAsync(
        Guid teamId, string groupPrefix,
        CancellationToken ct = default)
    {
        try
        {
            var team = await _dbContext.Teams.FindAsync([teamId], ct);
            if (team is null)
            {
                return new GroupLinkActionResult(false, ErrorMessage: "Team not found.");
            }

            var normalizedPrefix = groupPrefix.Trim().ToLowerInvariant();
            var previousPrefix = team.GoogleGroupPrefix;
            team.GoogleGroupPrefix = normalizedPrefix;

            var linkResult = await _googleSyncService.EnsureTeamGroupAsync(teamId);
            if (linkResult.RequiresConfirmation)
            {
                await _dbContext.SaveChangesAsync(ct);
                return new GroupLinkActionResult(true,
                    InfoMessage: $"Linked group for team \"{team.Name}\". Note: {linkResult.WarningMessage}");
            }

            if (linkResult.ErrorMessage is not null)
            {
                team.GoogleGroupPrefix = previousPrefix;
                return new GroupLinkActionResult(false,
                    ErrorMessage: $"Could not link group: {linkResult.ErrorMessage}");
            }

            await _dbContext.SaveChangesAsync(ct);
            return new GroupLinkActionResult(true,
                Message: $"Successfully linked {normalizedPrefix}@nobodies.team to team \"{team.Name}\".");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to link group {GroupPrefix} to team {TeamId}", groupPrefix, teamId);
            return new GroupLinkActionResult(false,
                ErrorMessage: $"Failed to link group: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<TeamSummary>> GetActiveTeamsAsync(
        CancellationToken ct = default)
    {
        return await _dbContext.Teams
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new TeamSummary(t.Id, t.Name))
            .ToListAsync(ct);
    }
}
