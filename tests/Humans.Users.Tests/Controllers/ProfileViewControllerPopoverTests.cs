using Humans.Users.Controllers;
using Humans.Users.Services;
using Humans.Users.Models;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Tickets.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Campaigns.Contracts;
using Humans.Camps.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Governance.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;

using Humans.Base.Enums;
using Humans.Base.Authorization;
using Humans.Base;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

using Humans.GoogleIntegration.Contracts;

namespace Humans.Users.Tests.Controllers;

public class ProfileViewControllerPopoverTests
{
    private readonly IUserServiceInternal _userService = Substitute.For<IUserServiceInternal>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IProfilePictureService _profilePictureService = Substitute.For<IProfilePictureService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuthorizationService _authorizationService = Substitute.For<IAuthorizationService>();
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly ProfileViewController _controller;
    private readonly Guid _viewerId = Guid.NewGuid();

    public ProfileViewControllerPopoverTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        var userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);

        var contextAccessor = Substitute.For<IHttpContextAccessor>();
        var claimsFactory = Substitute.For<IUserClaimsPrincipalFactory<User>>();
        var identityOptions = Substitute.For<IOptions<IdentityOptions>>();
        identityOptions.Value.Returns(new IdentityOptions());
        var schemeProvider = Substitute.For<IAuthenticationSchemeProvider>();
        var userConfirmation = Substitute.For<IUserConfirmation<User>>();
        var signInManager = Substitute.For<SignInManager<User>>(
            userManager, contextAccessor, claimsFactory, identityOptions,
            NullLogger<SignInManager<User>>.Instance, schemeProvider, userConfirmation);

        var localizer = Substitute.For<IStringLocalizer<UsersResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        var sharedLocalizer = Substitute.For<IStringLocalizer<SharedResource>>();
        sharedLocalizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new ProfileViewController(
            _userService,
            _profilePictureService,
            Substitute.For<IEmailService>(),
            Substitute.For<IEmailMessageFactory>(),
            Substitute.For<ICommunicationPreferenceService>(),
            Substitute.For<IAuditLogService>(),
            Substitute.For<IShiftSignups>(),
            Substitute.For<IBurnSettingsService>(),
            Substitute.For<IShiftManagementServiceRead>(),
            localizer,
            sharedLocalizer,
            _teamService,
            _campService,
            _authorizationService);

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, _viewerId.ToString()),
            new Claim(RoleChecks.UserStateClaimType, UserState.Active.ToString()),
        ], authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());

        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
    }

    [HumansFact]
    public async Task Popover_UnknownUser_Returns404()
    {
        var id = Guid.NewGuid();
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        var result = await _controller.Popover(id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Popover_UserWithoutProfile_RendersFallbackWithoutEmail()
    {
        // The popover is reachable by any authenticated user. Surfacing email
        // (verified or not) for the imported-no-profile path is a GDPR PII leak.
        // Admins who need email use /Users/Admin/{id}.
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            BurnerName = "Imported Human",
            DisplayName = "Imported Human",
            Email = "stale-legacy@example.com",
            PreferredLanguage = "es",
            ProfilePictureUrl = null,
        };
        var userEmails = new List<UserEmail>
        {
            new() { Id = Guid.NewGuid(), UserId = id, Email = "primary@example.com", IsVerified = true, IsPrimary = true },
        };
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(user, profile: null, userEmails));

        var result = await _controller.Popover(id, Xunit.TestContext.Current.CancellationToken);

        var partial = result.Should().BeOfType<PartialViewResult>().Subject;
        partial.ViewName.Should().Be("_HumanPopover");
        var vm = partial.Model.Should().BeOfType<ProfileSummaryViewModel>().Subject;
        vm.HasProfile.Should().BeFalse();
        vm.DisplayName.Should().Be("Imported Human");
        vm.Email.Should().BeNull();
        vm.PreferredLanguage.Should().Be("es");
        vm.MembershipTier.Should().BeNull();
        vm.MembershipStatus.Should().BeNull();
        vm.City.Should().BeNull();
        vm.Teams.Should().BeEmpty();
        vm.Languages.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Popover_UserWithProfile_RendersFullCardWithHasProfileTrue()
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            BurnerName = "Active Human",
            DisplayName = "Active Human",
            Email = "active@example.com",
            PreferredLanguage = "en",
        };
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = id,
            MembershipTier = MembershipTier.Volunteer,
            IsApproved = true,
            City = "Madrid",
            CountryCode = "ES",
        };
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(user, profile, userEmails: null));

        var result = await _controller.Popover(id, Xunit.TestContext.Current.CancellationToken);

        var partial = result.Should().BeOfType<PartialViewResult>().Subject;
        partial.ViewName.Should().Be("_HumanPopover");
        var vm = partial.Model.Should().BeOfType<ProfileSummaryViewModel>().Subject;
        vm.HasProfile.Should().BeTrue();
        vm.DisplayName.Should().Be("Active Human");
        vm.MembershipTier.Should().Be(nameof(MembershipTier.Volunteer));
        vm.MembershipStatus.Should().Be("Active");
        vm.City.Should().Be("Madrid");
        vm.CountryCode.Should().Be("ES");
    }

    [HumansFact]
    public async Task Popover_UserInCampWithRoles_PopulatesCampNameAndRoles()
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            DisplayName = "Camp Human",
            Email = "camp@example.com",
            PreferredLanguage = "en",
        };
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = id,
            MembershipTier = MembershipTier.Volunteer,
            IsApproved = true,
        };
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(user, profile, userEmails: null));

        var season = new CampSeasonInfo(
            Guid.NewGuid(), Guid.NewGuid(), "camp-funhouse", 2026, null,
            "Camp Funhouse", "", "", [],
            CampSeasonStatus.Active, YesNoMaybe.Yes, YesNoMaybe.No,
            AdultPlayspacePolicy.No, 0, null, null, null, 0, null, null);
        _campService.GetCampUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(new CampUserInfo(season, ["Camp Lead", "Greeter"]));

        var result = await _controller.Popover(id, Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ProfileSummaryViewModel>().Subject;
        vm.CampName.Should().Be("Camp Funhouse");
        vm.CampRoles.Should().Equal("Camp Lead", "Greeter");
    }

    [HumansTheory]
    [InlineData(UserState.Suspended)]
    [InlineData(UserState.AdminSuspended)]
    public async Task Popover_ActiveViewer_CanSeeSuspendedTargetsBasicIdentity(UserState targetState)
    {
        var id = Guid.NewGuid();
        var user = new User { Id = id, BurnerName = "Suspended Human", DisplayName = "Suspended Human", State = targetState };
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = id,
            MembershipTier = MembershipTier.Volunteer,
            City = "Madrid",
            CountryCode = "ES",
        };
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(user, profile, userEmails: null));

        var result = await _controller.Popover(id, Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ProfileSummaryViewModel>().Subject;
        vm.DisplayName.Should().Be("Suspended Human");
        vm.City.Should().Be("Madrid");
        vm.IsSuspended.Should().BeTrue();
    }

    [HumansFact]
    public async Task ViewProfile_SuspendedTarget_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        var user = new User { Id = id, DisplayName = "Suspended Human", State = UserState.Suspended };
        var profile = new Profile { Id = Guid.NewGuid(), UserId = id, MembershipTier = MembershipTier.Volunteer, IsApproved = true };
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(user, profile, userEmails: null));

        var result = await _controller.ViewProfile(id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    private static UserInfo BuildUserInfo(User user, Profile? profile, IReadOnlyList<UserEmail>? userEmails) =>
        UserInfoFactory.Create(
            user: user,
            userEmails: userEmails ?? [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
}
