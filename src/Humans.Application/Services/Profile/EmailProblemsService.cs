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

    public Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default)
    {
        var problems = new List<EmailProblem>();
        // Tasks 8–13 fill this in.
        return Task.FromResult(new EmailProblemsReport(_clock.GetCurrentInstant(), problems));
    }
}
