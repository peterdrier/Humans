using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Consent.Contracts;
using Humans.Consent.Services;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Services;

public sealed class ConsentNotificationMetersTests : IDisposable
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IConsentService _consent = Substitute.For<IConsentService>();
    private readonly ServiceProvider _localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
    private readonly ConsentNotificationMeters _sut;

    public ConsentNotificationMetersTests()
    {
        _consent.GetConsentReviewsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, ConsentReviewInfo>());
        _sut = new ConsentNotificationMeters(
            _users, _consent, _localization.GetRequiredService<IStringLocalizer<ConsentResource>>());
    }

    public void Dispose() => _localization.Dispose();

    [HumansTheory]
    [InlineData(ConsentRoles.ConsentCoordinator)]
    [InlineData(RoleNames.Admin)]
    public async Task Reviewers_SeeThePendingCountExcludingApprovedMembers(string role)
    {
        var users = NeedsConsentReview(3);
        var approved = users.First().Id;
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(users);
        _consent.GetConsentReviewsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, ConsentReviewInfo>
        {
            [approved] = ConsentReviewInfo.None(approved) with { Status = ConsentCheckStatus.Cleared, IsApproved = true },
        });

        var meters = await _sut.GetMetersAsync(Viewer(role), Guid.NewGuid(), TestContext.Current.CancellationToken);

        var meter = meters.Should().ContainSingle().Subject;
        meter.Title.Should().Be("Consent reviews pending");
        meter.Count.Should().Be(2);
        meter.ActionUrl.Should().Be("/Consent/ReviewQueue");
        meter.Audience.Should().Be("Consent Coordinators");
        meter.Priority.Should().Be(10);
    }

    [HumansTheory]
    [InlineData(RoleNames.Board, "the Board")]
    [InlineData(ShiftsRoles.ShiftsAdmin, "Shifts Admins")]
    public async Task BoardAndShiftsAdmin_SeeTheSameCountAsOnboardingProfiles(string role, string audience)
    {
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(NeedsConsentReview(1));

        var meters = await _sut.GetMetersAsync(Viewer(role), Guid.NewGuid(), TestContext.Current.CancellationToken);

        var meter = meters.Should().ContainSingle().Subject;
        meter.Title.Should().Be("Onboarding profiles pending");
        meter.Count.Should().Be(1);
        meter.ActionUrl.Should().Be("/Consent/ReviewQueue");
        meter.Audience.Should().Be(audience);
        meter.Priority.Should().Be(6);
    }

    [HumansFact]
    public async Task OtherViewers_GetNoMeterAndNoReads()
    {
        var meters = await _sut.GetMetersAsync(Viewer(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        meters.Should().BeEmpty();
        await _users.DidNotReceiveWithAnyArgs().GetAllUserInfosAsync(default);
    }

    [HumansFact]
    public async Task NothingPending_ShowsNoMeter()
    {
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<UserInfo>());

        var meters = await _sut.GetMetersAsync(
            Viewer(RoleNames.Admin, RoleNames.Board), Guid.NewGuid(), TestContext.Current.CancellationToken);

        meters.Should().BeEmpty();
    }

    private static ClaimsPrincipal Viewer(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "test"));

    private static IReadOnlyCollection<UserInfo> NeedsConsentReview(int count) =>
        Enumerable.Range(0, count).Select(_ => UserInfo.Create(
            user: new User
            {
                Id = Guid.NewGuid(),
                DisplayName = "U",
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                BurnerName = "B",
                FirstName = "F",
                LastName = "L",
            },
            userEmails: [],
            externalLogins: [],
            profile: UserFixtures.Profile())).ToList();
}
