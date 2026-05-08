using Humans.Application.DTOs.EmailProblems;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Helpers;
using NodaTime;

namespace Humans.Application.Services.Profile;

public sealed class EmailProblemsService : IEmailProblemsService
{
    private readonly IProfileService _profileService;
    private readonly IUserEmailService _userEmailService;
    private readonly IUserService _userService;
    private readonly IClock _clock;

    public EmailProblemsService(
        IProfileService profileService,
        IUserEmailService userEmailService,
        IUserService userService,
        IClock clock)
    {
        _profileService = profileService;
        _userEmailService = userEmailService;
        _userService = userService;
        _clock = clock;
    }

    public async Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default)
    {
        var problems = new List<EmailProblem>();

        var users = await _userService.GetAllUsersAsync(ct);
        var profiles = new List<FullProfile>(users.Count);
        foreach (var u in users)
        {
            var fp = await _profileService.GetFullProfileAsync(u.Id, ct);
            if (fp is not null) profiles.Add(fp);
        }

        foreach (var p in profiles)
        {
            var emails = p.AllUserEmails;

            if (emails.Count(e => e.IsPrimary) > 1)
                problems.Add(new EmailProblem(
                    EmailProblemKind.MultipleIsPrimary, p.UserId, null, null, null, null));

            if (emails.Count(e => e.IsGoogle) > 1)
                problems.Add(new EmailProblem(
                    EmailProblemKind.MultipleIsGoogle, p.UserId, null, null, null, null));

            if (emails.Any(e => e.IsVerified) && !emails.Any(e => e.IsPrimary))
                problems.Add(new EmailProblem(
                    EmailProblemKind.ZeroIsPrimary, p.UserId, null, null, null, null));

            if (!emails.Any(e => e.IsGoogle))
                problems.Add(new EmailProblem(
                    EmailProblemKind.ZeroIsGoogle, p.UserId, null, null, null, null));

            foreach (var unverified in emails.Where(e => !e.IsVerified))
            {
                problems.Add(new EmailProblem(
                    EmailProblemKind.Unverified, p.UserId, null,
                    unverified.Id, unverified.Email, null));
            }
        }

        // Cross-user duplicates: build normalized-email -> userIds map, flag pairs.
        var normToUsers = new Dictionary<string, List<(Guid UserId, string Raw)>>(StringComparer.Ordinal);
        foreach (var p in profiles)
        {
            foreach (var email in p.AllUserEmails)
            {
                var norm = EmailNormalization.NormalizeForComparison(email.Email);
                if (!normToUsers.TryGetValue(norm, out var list))
                {
                    list = new List<(Guid, string)>();
                    normToUsers[norm] = list;
                }
                list.Add((p.UserId, email.Email));
            }
        }

        foreach (var kvp in normToUsers)
        {
            var distinctUsers = kvp.Value.Select(t => t.UserId).Distinct().ToList();
            if (distinctUsers.Count <= 1) continue;

            for (var i = 0; i < distinctUsers.Count; i++)
            {
                for (var j = i + 1; j < distinctUsers.Count; j++)
                {
                    var rawA = kvp.Value.First(t => t.UserId == distinctUsers[i]).Raw;
                    problems.Add(new EmailProblem(
                        EmailProblemKind.SharedAcrossUsers,
                        distinctUsers[i], distinctUsers[j],
                        null, rawA, null));
                }
            }
        }

        var orphans = await _userEmailService.GetOrphanUserEmailsAsync(ct);
        foreach (var o in orphans)
        {
            problems.Add(new EmailProblem(
                EmailProblemKind.OrphanUserEmail, o.UserId, null, o.Id, o.Email, null));
        }

        var ghosts = await _userService.GetUsersWithLoginsButNoEmailsAsync(ct);
        foreach (var ghostId in ghosts)
        {
            problems.Add(new EmailProblem(
                EmailProblemKind.GhostExternalLogins, ghostId, null, null, null, null));
        }

        // Case 9: legacy AspNetIdentity Email column populated but no matching
        // verified UserEmail row exists. Pre-decoupling users whose row was
        // never written when the spec switched. Detect via the raw column
        // (IdentityEmailColumn) so the User.Email override's UserEmails-based
        // resolution doesn't mask it.
        foreach (var u in users)
        {
            var legacy = u.IdentityEmailColumn;
            if (string.IsNullOrEmpty(legacy)) continue;

            var profile = profiles.FirstOrDefault(p => p.UserId == u.Id);
            var emails = profile?.AllUserEmails ?? Array.Empty<UserEmailSnapshot>();
            var hasMatchingVerifiedRow = emails.Any(e =>
                e.IsVerified && string.Equals(e.Email, legacy, StringComparison.OrdinalIgnoreCase));
            if (hasMatchingVerifiedRow) continue;

            problems.Add(new EmailProblem(
                EmailProblemKind.LegacyIdentityEmailNotInUserEmails,
                u.Id, null, null, legacy, null));
        }

        return new EmailProblemsReport(_clock.GetCurrentInstant(), problems);
    }

    public async Task<bool> UsersShareAnyEmailAsync(Guid user1Id, Guid user2Id, CancellationToken ct = default)
    {
        if (user1Id == user2Id) return false;

        var p1 = await _profileService.GetFullProfileAsync(user1Id, ct);
        var p2 = await _profileService.GetFullProfileAsync(user2Id, ct);
        if (p1 is null || p2 is null) return false;

        var norms1 = p1.AllUserEmails
            .Select(e => EmailNormalization.NormalizeForComparison(e.Email))
            .ToHashSet(StringComparer.Ordinal);
        return p2.AllUserEmails
            .Any(e => norms1.Contains(EmailNormalization.NormalizeForComparison(e.Email)));
    }

    public async Task<bool> IsGhostExternalLoginsUserAsync(Guid userId, CancellationToken ct = default)
    {
        var ghosts = await _userService.GetUsersWithLoginsButNoEmailsAsync(ct);
        return ghosts.Contains(userId);
    }

    public async Task<IReadOnlyList<(Guid UserId, string Email)>> BackfillLegacyIdentityEmailsAsync(
        CancellationToken ct = default)
    {
        var users = await _userService.GetAllUsersAsync(ct);
        var backfilled = new List<(Guid, string)>();

        foreach (var u in users)
        {
            var legacy = u.IdentityEmailColumn;
            if (string.IsNullOrEmpty(legacy)) continue;

            var profile = await _profileService.GetFullProfileAsync(u.Id, ct);
            var emails = profile?.AllUserEmails ?? Array.Empty<UserEmailSnapshot>();
            if (emails.Any(e => e.IsVerified
                && string.Equals(e.Email, legacy, StringComparison.OrdinalIgnoreCase)))
                continue;

            await _userEmailService.AddVerifiedEmailAsync(u.Id, legacy, ct);
            backfilled.Add((u.Id, legacy));
        }

        return backfilled;
    }
}
