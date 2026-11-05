using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Helpers;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Users.Services;

internal sealed class UserEmailReader(
    IUserRepository repository,
    IUserService userService,
    IClock clock,
    IServiceProvider serviceProvider) : IApplicationService
{
    // Lazy: merge resolution reaches Teams, whose service also reads email data.
    private IAccountMergeService MergeService => serviceProvider.GetRequiredService<IAccountMergeService>();

    public async Task<IReadOnlyList<UserEmailEditDto>> GetUserEmailsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        if (info is null) return [];

        var emailIds = info.UserEmails.Select(e => e.Id).ToList();
        var mergePendingSet = await MergeService.GetPendingEmailIdsAsync(emailIds, cancellationToken);
        var verifiedCount = info.UserEmails.Count(e => e.IsVerified);

        return info.UserEmails.Select(e => new UserEmailEditDto(
            e.Id,
            e.Email,
            e.IsVerified,
            e.Provider,
            e.ProviderKey,
            e.IsPrimary,
            e.Visibility,
            IsPendingVerification: !e.IsVerified && e.VerificationSentAt.HasValue,
            IsMergePending: mergePendingSet.Contains(e.Id),
            CanUnlink: !string.IsNullOrEmpty(e.Provider) && !string.IsNullOrEmpty(e.ProviderKey)
                && verifiedCount - (e.IsVerified ? 1 : 0) >= 1
        )).ToList();
    }

    public async Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default)
    {
        var pendingRecord = await repository.GetUserEmailByIdReadOnlyAsync(pendingEmailId, ct);

        if (pendingRecord?.VerificationSentAt.HasValue == true)
        {
            var now = clock.GetCurrentInstant();
            var cooldownEnd = pendingRecord.VerificationSentAt.Value.Plus(Duration.FromMinutes(5));
            if (now < cooldownEnd)
            {
                var minutesUntilResend = (int)Math.Ceiling((cooldownEnd - now).TotalMinutes);
                return (false, minutesUntilResend, pendingEmailId);
            }
        }

        return (true, 0, null);
    }

    public async Task<IReadOnlyList<UserEmailDto>> GetVisibleEmailsAsync(
        Guid userId, ContactFieldVisibility accessLevel,
        CancellationToken cancellationToken = default)
    {
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        if (info is null) return [];

        var allowed = ContactFieldVisibilityRules.GetAllowedVisibilities(accessLevel);

        return info.UserEmails
            .Where(e => e.IsVerified && e.Visibility != null && allowed.Contains(e.Visibility!.Value))
            .Select(e => new UserEmailDto(
                e.Id,
                e.Email,
                e.IsVerified,
                e.Provider,
                e.ProviderKey,
                e.IsPrimary,
                e.Visibility))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNotificationTargetEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        // Resolve first: a tombstone's own address is a sentinel that bounces, and its
        // notification rows moved to the survivor. The key stays the requested id.
        var resolved = await userService.GetUserInfosAsync(userIds, cancellationToken);

        var result = new Dictionary<Guid, string>(userIds.Count);
        foreach (var userId in userIds)
        {
            if (!resolved.TryGetValue(userId, out var user))
                continue;

            if (!string.IsNullOrWhiteSpace(user.Email))
                result[userId] = user.Email;
        }

        return result;
    }

    public async Task<IReadOnlyList<UserEmailRowSnapshot>> FindByAddressAsync(
        string email, bool aliased, bool verifiedOnly, CancellationToken cancellationToken = default)
    {
        // EmailsMatch folds gmail/googlemail on both sides, so the aliased form needs no
        // separate alternate-address pass.
        email = email.Trim();
        var infos = await userService.GetAllUserInfosAsync(cancellationToken);
        return infos
            .SelectMany(i => i.UserEmails
                .Where(e => (!verifiedOnly || e.IsVerified) &&
                            (aliased
                                ? EmailNormalization.EmailsMatch(e.Email, email)
                                : string.Equals(e.Email, email, StringComparison.OrdinalIgnoreCase)))
                .Select(e => ToSnapshot(i.Id, e)))
            .ToList();
    }

    public Task<IReadOnlyList<Guid>> GetUserIdsByEmailPrefixAndSuffixAsync(
        string prefix,
        string suffix,
        CancellationToken cancellationToken = default) =>
        repository.GetUserIdsByUserEmailPrefixAndSuffixAsync(prefix, suffix, cancellationToken);

    public async Task<IReadOnlyList<UserEmailRowSnapshot>> GetEntitiesByUserIdAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // Raw: callers edit and delete rows by the owner id stamped on them, so a resolving read
        // would hand a tombstone's caller the survivor's rows stamped with the archived id.
        var info = await userService.GetRawUserInfoAsync(userId, cancellationToken);
        if (info is null) return [];
        return info.UserEmails.Select(e => ToSnapshot(userId, e)).ToList();
    }

    private static UserEmailRowSnapshot ToSnapshot(Guid userId, UserEmailInfo email) =>
        new(
            email.Id,
            userId,
            email.Email,
            email.IsVerified,
            email.Provider,
            email.ProviderKey,
            email.IsPrimary,
            email.Visibility,
            email.VerificationSentAt,
            email.CreatedAt,
            email.UpdatedAt);

    public async Task<IReadOnlyDictionary<Guid, string>> GetNotificationEmailsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        var requestedIds = userIds.ToHashSet();
        var users = await userService.GetAllRawUserInfosAsync(cancellationToken);
        return users
            .Where(user => requestedIds.Contains(user.Id) && user.PrimaryEmail != null)
            .ToDictionary(user => user.Id, user => user.PrimaryEmail!);
    }

    public async Task<IReadOnlyList<UserEmailMatch>> MatchByEmailsAsync(
        IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default)
    {
        var rows = await repository.GetUserEmailsByEmailsAsync(emails, cancellationToken);
        return rows
            .Select(r => new UserEmailMatch(
                r.Email, r.UserId, r.IsPrimary, r.IsVerified, r.UpdatedAt))
            .ToList();
    }
    public async Task<IReadOnlyList<UserEmailFlagViolation>> GetEmailFlagViolationsAsync(
        CancellationToken cancellationToken = default)
    {
        var infos = await userService.GetAllUserInfosAsync(cancellationToken);

        var violations = new List<UserEmailFlagViolation>();
        foreach (var info in infos)
        {
            if (info.UserEmails.Count == 0) continue;

            var verified = info.UserEmails.Where(e => e.IsVerified).ToList();
            var verifiedPrimaryCount = verified.Count(e => e.IsPrimary);
            var hasPrimaryProblem = verified.Count > 0 && verifiedPrimaryCount != 1;

            if (!hasPrimaryProblem) continue;

            violations.Add(new UserEmailFlagViolation(
                info.Id,
                verified.Count,
                verifiedPrimaryCount,
                hasPrimaryProblem));
        }

        return violations;
    }

    public async Task<IReadOnlyList<UserEmailOrphan>> GetOrphanUserEmailsAsync(CancellationToken ct = default)
    {
        // Iterating UserInfo can't find rows for missing users, so this needs the full-table scan.
        var allEmails = await repository.GetAllUserEmailsAsync(ct);
        var liveUserIds = (await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Select(u => u.Id)
            .ToHashSet();

        return allEmails
            .Where(e => !liveUserIds.Contains(e.UserId))
            .Select(e => new UserEmailOrphan(e.UserId, e.Id, e.Email))
            .ToList();
    }

}
