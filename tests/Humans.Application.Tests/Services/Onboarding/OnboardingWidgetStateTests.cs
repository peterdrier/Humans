using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Onboarding;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Onboarding;

public class OnboardingWidgetStateTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IShiftSignupService _signups = Substitute.For<IShiftSignupService>();
    private readonly IMembershipCalculator _membership = Substitute.For<IMembershipCalculator>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IConsentService _consents = Substitute.For<IConsentService>();
    private readonly IOnboardingWidgetSessionState _session = Substitute.For<IOnboardingWidgetSessionState>();

    public OnboardingWidgetStateTests()
    {
        // Default: no skip flag set (new user fresh through the widget). Individual
        // tests override this when exercising the skip-active path.
        _session.ShiftSkipActive.Returns(false);
        // Default: no signed required consents (new user). Individual tests
        // override this when exercising the returning-member path.
        _consents.GetRequiredConsentRowsForUserAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private OnboardingWidgetState BuildSut() =>
        new(_users, _signups, _membership, _shiftMgmt, _consents, _session);

    private static UserInfo NonStubUserInfo(Guid userId) =>
        WrapInUserInfo(userId, new Profile
        {
            UserId = userId,
            BurnerName = "Burner",
            FirstName = "First",
            LastName = "Last",
            State = ProfileState.Active,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        });

    private static UserInfo StubUserInfo(Guid userId) =>
        WrapInUserInfo(userId, new Profile
        {
            UserId = userId,
            BurnerName = "",
            FirstName = "",
            LastName = "",
            State = ProfileState.Stub,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        });

    private static UserInfo WrapInUserInfo(Guid userId, Profile? profile) => UserInfo.Create(
        user: new User
        {
            Id = userId,
            DisplayName = "Test",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: profile,
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);

    [HumansFact]
    public async Task ConsentsComplete_ShortCircuitsToComplete_EvenWithoutSignup()
    {
        var userId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, CancellationToken.None)
            .Returns(true);

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Complete, step);
    }

    [HumansFact]
    public async Task NoUserInfo_ReturnsNames()
    {
        var userId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, CancellationToken.None)
            .Returns(false);
        _users.GetUserInfoAsync(userId, CancellationToken.None).Returns((UserInfo?)null);

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Names, step);
    }

    [HumansFact]
    public async Task StubProfile_ReturnsNames()
    {
        // The bug fix: a Stub profile (created by EnsureStubProfileAsync at
        // signup) means the user hasn't filled in legal name yet — they must
        // hit the Names step before the consent flow can write a record.
        var userId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, CancellationToken.None)
            .Returns(false);
        _users.GetUserInfoAsync(userId, CancellationToken.None).Returns(StubUserInfo(userId));

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Names, step);
    }

    [HumansFact]
    public async Task ProfileButNoSignupAndNoSkip_ReturnsShifts()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, CancellationToken.None)
            .Returns(false);
        _users.GetUserInfoAsync(userId, CancellationToken.None).Returns(NonStubUserInfo(userId));
        _shiftMgmt.GetActiveAsync()
            .Returns(new EventSettings { Id = eventId });
        _signups.GetActiveSignupStatusesAsync(userId, eventId)
            .Returns((new HashSet<Guid>(), new Dictionary<Guid, SignupStatus>()));

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Shifts, step);
    }

    [HumansFact]
    public async Task ProfileWithSkipFlag_ReturnsConsents()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, CancellationToken.None)
            .Returns(false);
        _users.GetUserInfoAsync(userId, CancellationToken.None).Returns(NonStubUserInfo(userId));
        _shiftMgmt.GetActiveAsync()
            .Returns(new EventSettings { Id = eventId });
        _signups.GetActiveSignupStatusesAsync(userId, eventId)
            .Returns((new HashSet<Guid>(), new Dictionary<Guid, SignupStatus>()));
        _session.ShiftSkipActive.Returns(true);

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Consents, step);
    }

    [HumansFact]
    public async Task ProfileWithCurrentEventSignup_ReturnsConsents()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, CancellationToken.None)
            .Returns(false);
        _users.GetUserInfoAsync(userId, CancellationToken.None).Returns(NonStubUserInfo(userId));
        _shiftMgmt.GetActiveAsync()
            .Returns(new EventSettings { Id = eventId });
        _signups.GetActiveSignupStatusesAsync(userId, eventId)
            .Returns((new HashSet<Guid> { shiftId },
                      new Dictionary<Guid, SignupStatus> { [shiftId] = SignupStatus.Pending }));

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Consents, step);
    }

    [HumansFact]
    public async Task ProfileWithSignedRequiredConsent_ReturnsConsents_NotShifts()
    {
        // Returning member: consents missing (e.g. annual expiration / new
        // required doc) but at least one current required doc is signed.
        // Should bypass the Shifts step.
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var signedDocId = Guid.NewGuid();
        var unsignedDocId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, CancellationToken.None)
            .Returns(false);
        _users.GetUserInfoAsync(userId, CancellationToken.None).Returns(NonStubUserInfo(userId));
        _consents.GetRequiredConsentRowsForUserAsync(
                userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns([
                new RequiredConsentRow(signedDocId, "Code of Conduct", Signed: true),
                new RequiredConsentRow(unsignedDocId, "Privacy Policy", Signed: false)
            ]);
        _shiftMgmt.GetActiveAsync()
            .Returns(new EventSettings { Id = eventId });
        _signups.GetActiveSignupStatusesAsync(userId, eventId)
            .Returns((new HashSet<Guid>(), new Dictionary<Guid, SignupStatus>()));

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Consents, step);
    }

    [HumansFact]
    public async Task ProfileWithNoActiveEvent_ReturnsShifts()
    {
        var userId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, CancellationToken.None)
            .Returns(false);
        _users.GetUserInfoAsync(userId, CancellationToken.None).Returns(NonStubUserInfo(userId));
        _shiftMgmt.GetActiveAsync().Returns((EventSettings?)null);

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Shifts, step);
    }
}
