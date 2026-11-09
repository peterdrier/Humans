using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>Workspace account views and rename diagnostics enriched with member and team-resource facts.</summary>
internal sealed class WorkspaceAccountDiagnostics(
    IGoogleWorkspaceUserService workspaceUserService,
    IUserService userService,
    IUserEmailService userEmailService,
    IGoogleIntegrationService workspaceIdentities,
    ITeamResourceManagement teamResourceService,
    ITeamService teamService,
    string workspaceDomain,
    ILogger logger) : IApplicationService
{
    internal async Task<WorkspaceAccountListResult> GetWorkspaceAccountListAsync(
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

    internal async Task<EmailRenameDetectionResult> DetectEmailRenamesAsync(
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
                    GoogleEmail = identities.TryGetValue(u.Id, out var identity) ? identity.Email : null,
                })
                .Where(x => x.GoogleEmail is not null &&
                    x.GoogleEmail.EndsWith($"@{workspaceDomain}", StringComparison.OrdinalIgnoreCase))
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
                            OldEmail = user.GoogleEmail!,
                            NewEmail = account.PrimaryEmail,
                            AffectedResourceCount = affectedResources
                        });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Email rename detection cancelled by caller");
            throw;
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
