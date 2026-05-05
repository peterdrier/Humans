using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Services.Onboarding;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Onboarding;

public class OnboardingWidgetStateTests
{
    private readonly IProfileService _profile = Substitute.For<IProfileService>();
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
            .Returns(Array.Empty<RequiredConsentRow>());
    }

    private OnboardingWidgetState BuildSut() =>
        new(_profile, _signups, _membership, _shiftMgmt, _consents, _session);

    [HumansFact]
    public async Task ConsentsComplete_ShortCircuitsToComplete_EvenWithoutSignup()
    {
        var userId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(true);

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Complete, step);
    }

    [HumansFact]
    public async Task NoProfile_ReturnsNames()
    {
        var userId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(false);
        _profile.GetProfileAsync(userId, default).Returns((Profile?)null);

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Names, step);
    }

    [HumansFact]
    public async Task ProfileButNoSignupAndNoSkip_ReturnsShifts()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(false);
        _profile.GetProfileAsync(userId, default)
            .Returns(new Profile { UserId = userId, BurnerName = "x", FirstName = "y", LastName = "z" });
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
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(false);
        _profile.GetProfileAsync(userId, default)
            .Returns(new Profile { UserId = userId });
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
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(false);
        _profile.GetProfileAsync(userId, default)
            .Returns(new Profile { UserId = userId });
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
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(false);
        _profile.GetProfileAsync(userId, default)
            .Returns(new Profile { UserId = userId });
        _consents.GetRequiredConsentRowsForUserAsync(
                userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RequiredConsentRow(signedDocId, "Code of Conduct", Signed: true),
                new RequiredConsentRow(unsignedDocId, "Privacy Policy", Signed: false),
            });
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
        _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default)
            .Returns(false);
        _profile.GetProfileAsync(userId, default)
            .Returns(new Profile { UserId = userId });
        _shiftMgmt.GetActiveAsync().Returns((EventSettings?)null);

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Shifts, step);
    }
}
