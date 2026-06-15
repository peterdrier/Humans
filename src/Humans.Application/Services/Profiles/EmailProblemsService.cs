using Humans.Application.DTOs.EmailProblems;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using NodaTime;

namespace Humans.Application.Services.Profiles;

public sealed class EmailProblemsService(IUserEmailService userEmailService, IUserService userService, IClock clock)
    : IEmailProblemsService
{
    public async Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default)
    {
        var problems = new List<EmailProblem>();

        var allInfos = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);

        // Merge-source / GDPR-deleted tombstones are not live accounts: their scrubbed sentinel
        // emails (…@merged.local / …@deleted.local) yield only false per-user hygiene positives,
        // so exclude them from the profile scan (and the legacy scan below). The orphan-email and
        // ghost-login scans are deliberately NOT filtered — a row still pointing at a tombstone is
        // genuine leftover data from an incomplete merge/deletion, and this page is the only place
        // an admin can clean it up from (see GetOrphanUserEmailsAsync).
        var profiled = allInfos.Where(i => i.Profile is not null && !i.IsTombstone).ToList();

        foreach (var p in profiled)
        {
            var emails = p.UserEmails;

            if (emails.Count(e => e.IsPrimary) > 1)
                problems.Add(new EmailProblem(
                    EmailProblemKind.MultipleIsPrimary, p.Id, null, null, null, null));

            if (emails.Count(e => e.IsGoogle) > 1)
                problems.Add(new EmailProblem(
                    EmailProblemKind.MultipleIsGoogle, p.Id, null, null, null, null));

            if (emails.Any(e => e.IsVerified) && !emails.Any(e => e.IsPrimary))
                problems.Add(new EmailProblem(
                    EmailProblemKind.ZeroIsPrimary, p.Id, null, null, null, null));

            if (!emails.Any(e => e.IsGoogle))
                problems.Add(new EmailProblem(
                    EmailProblemKind.ZeroIsGoogle, p.Id, null, null, null, null));

            foreach (var unverified in emails.Where(e => !e.IsVerified))
            {
                problems.Add(new EmailProblem(
                    EmailProblemKind.Unverified, p.Id, null,
                    unverified.Id, unverified.Email, null));
            }
        }

        // Cross-user duplicate detection lives on the Account merges page now
        // (DuplicateAccountService.DetectDuplicatesAsync) — not in this scan.

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

        // Case 9: legacy AspNetIdentity.Email populated but no matching verified UserEmail row.
        foreach (var info in allInfos)
        {
            if (info.IsTombstone) continue; // scrubbed sentinel email — not a real hygiene problem

            var legacy = info.IdentityEmailColumn;
            if (string.IsNullOrEmpty(legacy)) continue;

            var hasMatchingVerifiedRow = info.UserEmails.Any(e =>
                e.IsVerified && string.Equals(e.Email, legacy, StringComparison.OrdinalIgnoreCase));
            if (hasMatchingVerifiedRow) continue;

            problems.Add(new EmailProblem(
                EmailProblemKind.LegacyIdentityEmailNotInUserEmails,
                info.Id, null, null, legacy, null));
        }

        return new EmailProblemsReport(clock.GetCurrentInstant(), problems);
    }

    public async Task<bool> IsGhostExternalLoginsUserAsync(Guid userId, CancellationToken ct = default)
    {
        var ghosts = await userService.GetUsersWithLoginsButNoEmailsAsync(ct);
        return ghosts.Contains(userId);
    }

    public async Task<IReadOnlyList<(Guid UserId, string Email)>> BackfillLegacyIdentityEmailsAsync(
        Guid actorUserId, CancellationToken ct = default)
    {
        // actorUserId captured by caller's audit row; per-row audit at controller.
        _ = actorUserId;

        var allInfos = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var backfilled = new List<(Guid, string)>();

        foreach (var info in allInfos)
        {
            // Never resurrect a tombstone: its scrubbed sentinel email must not become a
            // verified UserEmail row.
            if (info.IsTombstone) continue;

            var legacy = info.IdentityEmailColumn;
            if (string.IsNullOrEmpty(legacy)) continue;

            if (info.UserEmails.Any(e => e.IsVerified
                && string.Equals(e.Email, legacy, StringComparison.OrdinalIgnoreCase)))
                continue;

            // see nobodies-collective/Humans#697 — plain verified row; reconcile attaches tag on next OAuth sign-in.
            await userEmailService.AddVerifiedEmailAsync(info.Id, legacy, ct);
            backfilled.Add((info.Id, legacy));
        }

        return backfilled;
    }
}
