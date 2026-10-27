using Humans.Users.Services;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base;
using Humans.Cantina.Contracts;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Users.Tests.Contributions;

public class ThingsToDoMedicalPromptTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICantinaService _cantina = Substitute.For<ICantinaService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly IStringLocalizer<SharedResource> _localizer = Substitute.For<IStringLocalizer<SharedResource>>();
    private readonly IServiceProvider _services;
    private readonly SectionThingsToDo _sut = new();

    public ThingsToDoMedicalPromptTests()
    {
        // Each [key] returns a LocalizedString whose Value == key.
        _localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        // A volunteer member, so the consent-check entry is skipped.
        _membershipCalculator.GetMembershipSnapshotAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(
                Status: MembershipStatus.Active,
                IsVolunteerMember: true,
                RequiredConsentCount: 0,
                PendingConsentCount: 0,
                MissingConsentVersionIds: []));

        _services = new ServiceCollection()
            .AddSingleton(_userService)
            .AddSingleton(_cantina)
            .AddSingleton(_shiftView)
            .AddSingleton(_membershipCalculator)
            .AddSingleton(_localizer)
            .AddSingleton<ILogger<SectionThingsToDo>>(NullLogger<SectionThingsToDo>.Instance)
            .BuildServiceProvider();
    }

    [HumansFact]
    public async Task MedicalPromptShowsWhileTheDietaryNudgeIsOpen()
    {
        var userId = StubUser(dietary: null, medical: null);

        var entries = await _sut.EntriesAsync(_services, Principal(userId));

        var medical = entries.Should().ContainSingle(e => e.Key == "medical").Subject;
        medical.Controller.Should().Be("Profile");
        medical.Action.Should().Be("Medical");
        medical.Description.Should().Be("Todo_Medical_Pending");
        medical.IsDone.Should().BeFalse();
    }

    [HumansFact]
    public async Task MedicalPromptGoesAwayOnceTheFoodQuestionIsAnswered()
    {
        var userId = StubUser(dietary: "Vegan", medical: null);

        var entries = await _sut.EntriesAsync(_services, Principal(userId));

        entries.Should().NotContain(e => e.Key == "medical", "members with nothing to declare are never nagged");
    }

    [HumansFact]
    public async Task MedicalPromptGoesAwayAfterIDontWantToAnswer()
    {
        var userId = StubUser(dietary: null, medical: null, declined: true);

        var entries = await _sut.EntriesAsync(_services, Principal(userId));

        entries.Should().NotContain(e => e.Key == "medical", "the opt-out closes the dietary nudge it rides with");
    }

    [HumansFact]
    public async Task MedicalPromptNotShownWhenConditionsAreOnFile()
    {
        var userId = StubUser(dietary: null, medical: "Epilepsy");

        var entries = await _sut.EntriesAsync(_services, Principal(userId));

        entries.Should().NotContain(e => e.Key == "medical");
        await _cantina.DidNotReceiveWithAnyArgs().GetDietaryProfileAsync(default);
    }

    [HumansFact]
    public async Task UsersNoLongerContributesTheDietaryNudge()
    {
        var userId = StubUser(dietary: null, medical: null);

        var entries = await _sut.EntriesAsync(_services, Principal(userId));

        entries.Should().NotContain(e => e.Key == "dietary-medical" || e.Key == "dietary");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AbortedNudgeRead_PropagatesCancellation(bool medical)
    {
        var id = StubUser(dietary: null, medical: null);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        if (medical)
            _cantina.GetDietaryProfileAsync(id, aborted.Token)
                .Returns(Task.FromCanceled<DietaryProfileInfo?>(aborted.Token));
        else
            _userService.GetUserInfoAsync(id, aborted.Token)
                .Returns(ValueTask.FromCanceled<UserInfo?>(aborted.Token));

        var load = () => _sut.EntriesAsync(_services, Principal(id), aborted.Token).AsTask();

        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    private Guid StubUser(string? dietary, string? medical, bool declined = false)
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(UserInfoWith(userId, medical));
        _cantina.GetDietaryProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(dietary is null && !declined
                ? null
                : new DietaryProfileInfo(userId, dietary, [], null, [], null, declined));
        _shiftView.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserSummary>(new ShiftUserSummary(userId, [], [])));
        return userId;
    }

    private static ClaimsPrincipal Principal(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private static UserInfo UserInfoWith(Guid userId, string? medical) => UserInfoFactory.Create(
        user: new User { Id = userId, DisplayName = "Test", PreferredLanguage = "en" },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: new Profile { UserId = userId, BurnerName = "Test", MedicalConditions = medical },
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);
}
