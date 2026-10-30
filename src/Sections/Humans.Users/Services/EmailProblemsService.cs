using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Services;

internal sealed class EmailProblemsService(
    IUserEmailService userEmailService,
    IUserManagement userService,
    IAuditLogService audit,
    IClock clock)
    : IEmailProblemsService
{
    public async Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default)
    {
        var problems = new List<EmailProblem>();

        var allInfos = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);

        // Tombstones' sentinel emails yield only false positives, so the profile and legacy scans
        // skip them. The orphan-email and ghost-login scans deliberately don't: a row still pointing
        // at a tombstone is leftover data an admin can only clean up here.
        var profiled = allInfos.Where(i => i.Profile is not null && !i.IsTombstone).ToList();

        foreach (var p in profiled)
        {
            var emails = p.UserEmails;

            if (emails.Count(e => e.IsPrimary) > 1)
                problems.Add(new EmailProblem(
                    EmailProblemKind.MultipleIsPrimary, p.Id, null, null, null, null));

            if (emails.Any(e => e.IsVerified) && !emails.Any(e => e.IsPrimary))
                problems.Add(new EmailProblem(
                    EmailProblemKind.ZeroIsPrimary, p.Id, null, null, null, null));

            foreach (var unverified in emails.Where(e => !e.IsVerified))
            {
                problems.Add(new EmailProblem(
                    EmailProblemKind.Unverified, p.Id, null,
                    unverified.Id, unverified.Email, null));
            }
        }

        var orphans = await userEmailService.GetOrphanUserEmailsAsync(ct);
        foreach (var o in orphans)
        {
            problems.Add(new EmailProblem(
                EmailProblemKind.OrphanUserEmail, o.UserId, null, o.EmailId, o.Email, null));
        }

        var ghosts = await userService.GetUsersWithLoginsButNoEmailsAsync(ct);
        foreach (var ghostId in ghosts)
        {
            problems.Add(new EmailProblem(
                EmailProblemKind.GhostExternalLogins, ghostId, null, null, null, null));
        }

        foreach (var (info, legacy) in GetUnmatchedLegacyEmails(allInfos))
        {
            problems.Add(new EmailProblem(
                EmailProblemKind.LegacyIdentityEmailNotInUserEmails,
                info.Id, null, null, legacy, null));
        }

        return new EmailProblemsReport(clock.GetCurrentInstant(), problems);
    }

    public async Task<IReadOnlyList<(Guid UserId, string Email)>> BackfillLegacyIdentityEmailsAsync(
        Guid actorUserId, CancellationToken ct = default)
    {
        var allInfos = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var backfilled = new List<(Guid, string)>();

        foreach (var (info, legacy) in GetUnmatchedLegacyEmails(allInfos))
        {
            // Plain verified row; the next OAuth sign-in attaches the provider tag.
            // A refused row stays unmatched, so the status list keeps showing it; the add logs the reason.
            var emailAddition = await userEmailService.AddVerifiedEmailAsync(info.Id, legacy, ct);
            if (emailAddition.ErrorKey is not null)
                continue;
            await audit.LogAsync(
                UsersAuditActions.LegacyIdentityEmailBackfilled, nameof(User), info.Id,
                $"Backfilled verified UserEmail row from legacy User.Email column: {legacy}",
                actorUserId);
            backfilled.Add((info.Id, legacy));
        }

        return backfilled;
    }

    private static IEnumerable<(UserInfo User, string Email)> GetUnmatchedLegacyEmails(IEnumerable<UserInfo> users)
    {
        foreach (var info in users)
        {
            // Never present or copy a tombstone's sentinel address as a real email.
            if (info.IsTombstone) continue;
            var legacy = info.IdentityEmailColumn;
            if (string.IsNullOrEmpty(legacy)) continue;
            if (info.UserEmails.Any(e => e.IsVerified
                && string.Equals(e.Email, legacy, StringComparison.OrdinalIgnoreCase))) continue;
            yield return (info, legacy);
        }
    }

}
