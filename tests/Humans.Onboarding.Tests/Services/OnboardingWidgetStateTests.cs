using Humans.Consent.Contracts;
using Humans.Base.Constants;
using Humans.Governance.Contracts;
using Humans.Onboarding.Services;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Onboarding.Tests.Services;

public class OnboardingWidgetStateTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IMembershipCalculator _membership = Substitute.For<IMembershipCalculator>();
    private readonly ISettingsService _burnSettings = Substitute.For<ISettingsService>();
    private readonly IConsentService _consents = Substitute.For<IConsentService>();
    private readonly IOnboardingWidgetSessionState _session = Substitute.For<IOnboardingWidgetSessionState>();

    public OnboardingWidgetStateTests()
    {
        // Default: no skip flag set (new user fresh through the widget). Individual
        // tests override this when exercising the skip-active path.
        _session.ShiftSkipActive.Returns(false);
        _consents.GetConsentedVersionIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        _shiftView.GetUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<ShiftUserSummary>(ShiftUserSummary.Empty(call.ArgAt<Guid>(0))));
    }

    private OnboardingWidgetState BuildSut() =>
        new(_users, _shiftView, _membership, _burnSettings, _consents, _session);

    private static UserInfo NonStubUserInfo(Guid userId) =>
        WrapInUserInfo(userId, UserFixtures.Profile(
            burnerName: "Burner",
            firstName: "First",
            lastName: "Last"));

    private static UserInfo StubUserInfo(Guid userId) =>
        WrapInUserInfo(userId, UserFixtures.Profile());

    private static UserInfo WrapInUserInfo(Guid userId, ProfileInfo? profile) => UserInfo.Create(
        user: new User
        {
            Id = userId,
            DisplayName = "Test",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            State = UserFixtures.StateFor(profile),
        },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: profile,
        communicationPreferences: []);

    [HumansFact]
    public async Task GetCurrentStep_CancelsTheActiveEventReadWhenTheRequestIsAbandoned()
    {
        using var request = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(NonStubUserInfo(userId));
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await request.CancelAsync();
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return null;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildSut().GetCurrentStepAsync(userId, request.Token));
        await _burnSettings.Received(1).GetActiveEventSettingsAsync(request.Token);
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Completion_RequiresNamesAndChecksTheSurvivingUser(bool mergedAlias, bool missingNames)
    {
        var userId = Guid.NewGuid();
        var inputId = mergedAlias ? Guid.NewGuid() : userId;
        var info = missingNames ? StubUserInfo(userId) : NonStubUserInfo(userId);
        _users.GetUserInfoAsync(inputId, Arg.Any<CancellationToken>()).Returns(info);
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(true);

        var step = await BuildSut().GetCurrentStepAsync(inputId, TestContext.Current.CancellationToken);

        Assert.Equal(missingNames ? OnboardingWidgetStep.Names : OnboardingWidgetStep.Complete, step);
    }

    [HumansFact]
    public async Task ConsentsComplete_ShortCircuitsToComplete_EvenWithoutSignup()
    {
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(NonStubUserInfo(userId));
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(true);

        var step = await BuildSut().GetCurrentStepAsync(userId, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingWidgetStep.Complete, step);
    }

    [HumansFact]
    public async Task NoUserInfo_ReturnsNames()
    {
        var userId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(false);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        var step = await BuildSut().GetCurrentStepAsync(userId, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingWidgetStep.Names, step);
    }

    [HumansFact]
    public async Task StubProfile_ReturnsNames()
    {
        // A Stub profile (created by EnsureStubProfileAsync at
        // signup) means the user hasn't filled in legal name yet — they must
        // hit the Names step before the consent flow can write a record.
        var userId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(false);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(StubUserInfo(userId));

        var step = await BuildSut().GetCurrentStepAsync(userId, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingWidgetStep.Names, step);
    }

    [HumansFact]
    public async Task ProfileButNoSignupAndNoSkip_ReturnsShifts()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(false);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(NonStubUserInfo(userId));
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(BurnFixtures.Burn(id: eventId));

        var step = await BuildSut().GetCurrentStepAsync(userId, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingWidgetStep.Shifts, step);
    }

    [HumansFact]
    public async Task ProfileWithSkipFlag_ReturnsConsents()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(false);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(NonStubUserInfo(userId));
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(BurnFixtures.Burn(id: eventId));
        _session.ShiftSkipActive.Returns(true);

        var step = await BuildSut().GetCurrentStepAsync(userId, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingWidgetStep.Consents, step);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileWithCurrentEventSignup_ReturnsConsents(bool mergedAlias)
    {
        var userId = Guid.NewGuid();
        var inputId = mergedAlias ? Guid.NewGuid() : userId;
        var eventId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(false);
        _users.GetUserInfoAsync(inputId, Arg.Any<CancellationToken>()).Returns(NonStubUserInfo(userId));
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(BurnFixtures.Burn(id: eventId));
        _shiftView.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ShiftUserSummary>(ShiftFixtures.UserSummary(
                userId,
                [ShiftFixtures.Signup(shiftId: shiftId, status: SignupStatus.Pending)])));

        var step = await BuildSut().GetCurrentStepAsync(inputId, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingWidgetStep.Consents, step);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PriorSignatureOnReplacedVersion_ReturnsConsents_ForTheSurvivingUser(bool mergedAlias)
    {
        var userId = Guid.NewGuid();
        var inputId = mergedAlias ? Guid.NewGuid() : userId;
        var ct = TestContext.Current.CancellationToken;
        _users.GetUserInfoAsync(inputId, Arg.Any<CancellationToken>()).Returns(NonStubUserInfo(userId));
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(false);
        _consents.GetRequiredConsentRowsForUserAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns([new RequiredConsentRow(Guid.NewGuid(), "Replacement document", Signed: false)]);
        _consents.GetConsentedVersionIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { Guid.NewGuid() });
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(BurnFixtures.Burn());

        var step = await BuildSut().GetCurrentStepAsync(inputId, ct);

        Assert.Equal(OnboardingWidgetStep.Consents, step);
        await _consents.Received(1).GetConsentedVersionIdsAsync(userId, ct);
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
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(false);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(NonStubUserInfo(userId));
        _consents.GetConsentedVersionIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { signedDocId });
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(BurnFixtures.Burn(id: eventId));

        var step = await BuildSut().GetCurrentStepAsync(userId, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingWidgetStep.Consents, step);
    }

    [HumansFact]
    public async Task ProfileWithNoActiveEvent_ReturnsShifts()
    {
        var userId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(false);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(NonStubUserInfo(userId));
        _burnSettings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettingsInfo?)null);

        var step = await BuildSut().GetCurrentStepAsync(userId, TestContext.Current.CancellationToken);

        Assert.Equal(OnboardingWidgetStep.Shifts, step);
    }
}
