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

        return new EmailProblemsReport(_clock.GetCurrentInstant(), problems);
    }
}
