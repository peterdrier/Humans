using Humans.Base.Authorization;
using Humans.Google.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Microsoft.AspNetCore.Identity;

namespace Humans.Users.Models;

internal sealed class EmailsViewModelBuilder(
    IUserManagement userService,
    UserManager<User> userManager,
    IUserEmailService userEmailService,
    IEnumerable<IUserEmailLock> emailLocks,
    GoogleWorkspaceOptions googleWorkspaceOptions)
{
    public async Task<EmailsViewModel> BuildAsync(
        User user, bool isAdminContext, bool viewerIsAdmin, CancellationToken ct = default)
    {
        var emails = await userEmailService.GetUserEmailsAsync(user.Id, ct);
        var info = await userService.GetUserInfoAsync(user.Id, ct);

        var (canAdd, minutesUntilResend) = await GetEmailAddCooldownStatusAsync(emails, ct);

        var workspaceLockedEmail = FindWorkspaceLockedEmail(emails);

        var (userLogins, rawUserEmails) = await GetAdminEmailDiagnosticsAsync(user.Id, isAdminContext, ct);

        var linkedAccounts = await BuildLinkedAccountsAsync(user, emails, isAdminContext, ct);

        // Locked addresses: the grid hides Delete, and DeleteEmailAsync re-validates.
        var lockedEmails = new List<string>();
        foreach (var emailLock in emailLocks)
            lockedEmails.AddRange(await emailLock.GetLockedAddressesAsync(user.Id, ct));

        bool RowIsTicketLinked(string address) =>
            lockedEmails.Any(locked => Base.Helpers.EmailNormalization.EmailsMatch(address, locked));

        bool RowHasOrphanProviderTag(string? provider, string? providerKey) =>
            isAdminContext
            && !string.IsNullOrEmpty(provider)
            && !string.IsNullOrEmpty(providerKey)
            && !userLogins.Any(l =>
                string.Equals(l.Provider, provider, StringComparison.Ordinal)
                && string.Equals(l.ProviderKey, providerKey, StringComparison.Ordinal));

        bool LoginHasOrphanRow(string provider, string providerKey) =>
            !emails.Any(e =>
                string.Equals(e.Provider, provider, StringComparison.Ordinal)
                && string.Equals(e.ProviderKey, providerKey, StringComparison.Ordinal));

        return new EmailsViewModel
        {
            Emails = emails.Select(e => new EmailRowViewModel
            {
                Id = e.Id,
                Email = e.Email,
                IsVerified = e.IsVerified,
                IsPrimary = e.IsPrimary,
                Visibility = e.Visibility,
                IsPendingVerification = e.IsPendingVerification,
                Provider = e.Provider,
                HasOrphanProviderTag = RowHasOrphanProviderTag(e.Provider, e.ProviderKey),
                IsTicketLinked = RowIsTicketLinked(e.Email),
            }).ToList(),
            ExternalLogins = userLogins.Select(l => new ExternalLoginRowViewModel
            {
                LoginProvider = l.Provider,
                ProviderKeyHash = UserEmailService.ShortProviderKeyHash(l.ProviderKey),
                ProviderDisplayName = null,
                HasOrphanLogin = LoginHasOrphanRow(l.Provider, l.ProviderKey),
            }).ToList(),
            RawUserEmails = rawUserEmails,
            LinkedAccounts = linkedAccounts,
            CanAddEmail = !UserStateEvaluator.IsTombstone(user) && (isAdminContext || canAdd),
            MinutesUntilResend = minutesUntilResend,
            TargetUserId = user.Id,
            IsAdminContext = isAdminContext,
            WorkspaceLockedEmailId = workspaceLockedEmail?.Id,
            LegacyIdentityEmailColumn = isAdminContext
                && viewerIsAdmin
                ? user.IdentityEmailColumn
                : null,
            TargetUserInfo = isAdminContext
                && viewerIsAdmin
                    ? info
                    : null,
        };
    }

    private async Task<(bool CanAdd, int MinutesUntilResend)> GetEmailAddCooldownStatusAsync(
        IReadOnlyList<UserEmailEditDto> emails, CancellationToken ct)
    {
        var pendingEmail = emails.FirstOrDefault(e => e.IsPendingVerification);
        if (pendingEmail is null)
            return (true, 0);

        var (cooldownCanAdd, cooldownMinutes, _) =
            await userEmailService.GetEmailCooldownInfoAsync(pendingEmail.Id, ct);
        return (cooldownCanAdd, cooldownMinutes);
    }

    // Provider Google on a Workspace-domain address: locks the Primary and Google radios.
    private UserEmailEditDto? FindWorkspaceLockedEmail(IReadOnlyList<UserEmailEditDto> emails)
    {
        var workspaceDomainSuffix = "@" + googleWorkspaceOptions.Domain;
        var workspaceCandidates = emails
            .Where(e => !string.IsNullOrEmpty(e.Provider)
                && string.Equals(e.Provider, "Google", StringComparison.OrdinalIgnoreCase)
                && e.Email.EndsWith(workspaceDomainSuffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return workspaceCandidates.FirstOrDefault(e => e.IsPrimary)
            ?? workspaceCandidates.FirstOrDefault();
    }

    // Admin diagnostic: the login rows beside the email rows, to show where the stores disagree.
    private async Task<(IReadOnlyList<(string Provider, string ProviderKey)> UserLogins, IReadOnlyList<UserEmailRowSnapshot> RawUserEmails)>
        GetAdminEmailDiagnosticsAsync(Guid userId, bool isAdminContext, CancellationToken ct)
    {
        IReadOnlyList<(string Provider, string ProviderKey)> userLogins = [];
        IReadOnlyList<UserEmailRowSnapshot> rawUserEmails = [];
        if (!isAdminContext)
            return (userLogins, rawUserEmails);

        var loginsByUser = await userService.GetExternalLoginsByUserIdsAsync([userId], ct);
        if (loginsByUser.TryGetValue(userId, out var list))
            userLogins = list;
        rawUserEmails = await userEmailService.GetEntitiesByUserIdAsync(userId, ct);
        return (userLogins, rawUserEmails);
    }

    // Self view goes through UserManager for ProviderDisplayName, then stitches each row's id and CreatedAt.
    private async Task<IReadOnlyList<LinkedOAuthAccountViewModel>> BuildLinkedAccountsAsync(
        User user, IReadOnlyList<UserEmailEditDto> emails, bool isAdminContext, CancellationToken ct)
    {
        if (isAdminContext)
            return [];

        var logins = await userManager.GetLoginsAsync(user);
        if (logins.Count == 0)
            return [];

        // (Provider, ProviderKey) uniqueness is service-enforced, not in the database: keep the first row per key.
        var rowsByKey = new Dictionary<(string, string), UserEmailRowSnapshot>();
        foreach (var r in await userEmailService.GetEntitiesByUserIdAsync(user.Id, ct))
        {
            if (string.IsNullOrEmpty(r.Provider) || string.IsNullOrEmpty(r.ProviderKey))
                continue;
            rowsByKey.TryAdd((r.Provider!, r.ProviderKey!), r);
        }

        return logins.Select(l =>
        {
            rowsByKey.TryGetValue((l.LoginProvider, l.ProviderKey), out var row);
            // Row-backed and orphan logins share the service's sign-in safety rule.
            var canUnlink = row is not null
                ? emails.FirstOrDefault(e => e.Id == row.Id)?.CanUnlink == true
                : UserEmailService.CanUnlink(emails.Count(e => e.IsVerified), removesVerifiedEmail: false);
            return new LinkedOAuthAccountViewModel
            {
                Provider = l.LoginProvider,
                ProviderKey = l.ProviderKey,
                ProviderDisplayName = l.ProviderDisplayName,
                ProviderKeyHash = UserEmailService.ShortProviderKeyHash(l.ProviderKey),
                Email = row?.Email,
                LinkedAt = row?.CreatedAt,
                CanUnlink = canUnlink,
            };
        }).ToList();
    }
}
