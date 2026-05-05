using Humans.Application.DTOs.EmailProblems;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
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

        var profiles = await _profileService.GetFullProfileSnapshotAsync(ct);

        foreach (var p in profiles)
        {
            var emails = p.AllUserEmails;

            if (emails.Count(e => e.IsPrimary) > 1)
                problems.Add(new EmailProblem(
                    EmailProblemKind.MultipleIsPrimary, p.UserId, null, null, null, null));

            if (emails.Count(e => e.IsGoogle) > 1)
                problems.Add(new EmailProblem(
                    EmailProblemKind.MultipleIsGoogle, p.UserId, null, null, null, null));
        }

        return new EmailProblemsReport(_clock.GetCurrentInstant(), problems);
    }
}
