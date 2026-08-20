using Humans.Users.Services;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Users.Tests.Contributions;

/// <summary>
/// Covers the dietary-medical nudge gate in Users' <see cref="SectionThingsToDo"/> contribution.
/// Spec: src/Sections/Humans.Users/Docs/features/dietary-medical-nudge.md (US-35.5)
/// </summary>
public class ThingsToDoDietaryGateTests
{
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly IShiftManagementServiceRead _shiftMgmt = Substitute.For<IShiftManagementServiceRead>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IMembershipCalculatorRead _membershipCalculator = Substitute.For<IMembershipCalculatorRead>();
    private readonly IStringLocalizer<SharedResource> _localizer = Substitute.For<IStringLocalizer<SharedResource>>();
    private readonly IServiceProvider _services;
    private readonly SectionThingsToDo _sut = new();

    public ThingsToDoDietaryGateTests()
    {
        // Each [key] returns a LocalizedString whose Value == key. Lets tests
        // assert against the key name (no resx lookup needed).
        _localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        // Default profile snapshot — a volunteer member, so the consent-check entry
        // is skipped and the list stays focused on what these tests care about.
        _membershipCalculator.GetMembershipSnapshotAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(
                Status: MembershipStatus.Active,
                IsVolunteerMember: true,
                RequiredConsentCount: 0,
                PendingConsentCount: 0,
                MissingConsentVersionIds: []));

        _services = new ServiceCollection()
            .AddSingleton(_userService)
            .AddSingleton(_shiftMgmt)
            .AddSingleton(_shiftView)
            .AddSingleton(_membershipCalculator)
            .AddSingleton(_localizer)
            .AddSingleton<ILogger<SectionThingsToDo>>(NullLogger<SectionThingsToDo>.Instance)
            .BuildServiceProvider();
    }

    [HumansFact]
    public async Task DietaryEntryAppearsWithNoShiftCopyWhenNoQualifyingSignup()
    {
        var userId = StubUser(dietary: null);
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId, Arg.Any<CancellationToken>()).Returns(false);

        var entries = await _sut.EntriesAsync(_services, Principal(userId));

        var dietary = entries.Should().ContainSingle(e => e.Key == "dietary-medical").Subject;
        dietary.IsDone.Should().BeFalse();
        dietary.Description.Should().Be("Todo_DietaryMedical_NoShift_Pending");
    }

    [HumansFact]
    public async Task DietaryEntryUsesExistingCopyWhenHasQualifyingSignup()
    {
        var userId = StubUser(dietary: null);
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId, Arg.Any<CancellationToken>()).Returns(true);

        var entries = await _sut.EntriesAsync(_services, Principal(userId));

        entries.Should().ContainSingle(e => e.Key == "dietary-medical")
            .Which.Description.Should().Be("Todo_DietaryMedical_Pending");
    }

    [HumansFact]
    public async Task DietaryEntryNotAddedWhenDietaryFilled()
    {
        var userId = StubUser(dietary: "Vegetarian");
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId, Arg.Any<CancellationToken>()).Returns(false);

        var entries = await _sut.EntriesAsync(_services, Principal(userId));

        entries.Should().NotContain(e => e.Key == "dietary-medical");
    }

    private Guid StubUser(string? dietary)
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(UserInfoWith(userId, dietary));
        _shiftView.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserSummary>(new ShiftUserSummary(userId, [], [])));
        return userId;
    }

    private static ClaimsPrincipal Principal(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private static UserInfo UserInfoWith(Guid userId, string? dietary) => UserInfoFactory.Create(
        user: new User { Id = userId, DisplayName = "Test", PreferredLanguage = "en" },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: new Profile { UserId = userId, BurnerName = "Test", DietaryPreference = dietary },
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);
}
