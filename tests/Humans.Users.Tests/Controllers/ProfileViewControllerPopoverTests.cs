using Humans.Consent.Contracts;
using Humans.Users.Tests.Infrastructure;
using Humans.Users.Controllers;
using Humans.Users.Services;
using Humans.Users.Models;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Governance.Contracts;
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
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.Controllers;

public class ProfileViewControllerPopoverTests
{
    private readonly Dictionary<Guid, ConsentReviewInfo> _reviews = [];
    private readonly IConsentService _consentService;
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IProfilePictureService _profilePictureService = Substitute.For<IProfilePictureService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IShiftSignups _shiftSignups = Substitute.For<IShiftSignups>();
    private readonly IShiftService _shiftManagement = Substitute.For<IShiftService>();
    private readonly IAuthorizationService _authorizationService = Substitute.For<IAuthorizationService>();
    private readonly ICampService _campService = Substitute.For<ICampService>();
    private readonly IStringLocalizer<SharedResource> _sharedLocalizer = Substitute.For<IStringLocalizer<SharedResource>>();
    private readonly ProfileViewController _controller;
    private readonly Guid _viewerId = Guid.NewGuid();

    public ProfileViewControllerPopoverTests()
    {
        _consentService = ConsentReviewStubs.Create(_reviews);
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

        _sharedLocalizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new ProfileViewController(
            _userService,
            _profilePictureService,
            _shiftSignups,
            _shiftManagement,
            _sharedLocalizer,
            _teamService,
            _campService,
            Substitute.For<IGovernanceService>(),
            _authorizationService,
            new MedicalConditionsService(
                Substitute.For<Humans.Users.Data.Repositories.IUserRepository>(), _userService,
                Substitute.For<Humans.AuditLog.Contracts.IAuditLogService>()));

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
    public async Task ViewProfile_OrdersNoShowHistoryAndPreservesCancellation()
    {
        using var source = new CancellationTokenSource();
        var targetId = Guid.NewGuid();
        _userService.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>())
            .Returns(BuildActiveUserInfo(_viewerId, "Viewer", "viewer@example.com"));
        _userService.GetUserInfoAsync(targetId, Arg.Any<CancellationToken>())
            .Returns(BuildActiveUserInfo(targetId, "Target", "target@example.com"));
        _shiftManagement.GetCoordinatorTeamIdsAsync(_viewerId, Arg.Any<CancellationToken>())
            .Returns(new[] { Guid.NewGuid() });
        var reviewerId = Guid.NewGuid();
        var missingReviewerId = Guid.NewGuid();
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());
        var reviewed = Instant.FromUtc(2026, 3, 1, 12, 0);
        _shiftSignups.GetNoShowHistoryAsync(targetId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new NoShowHistoryEntry("Undated", Guid.Empty, reviewed, "UTC", null, null),
                new NoShowHistoryEntry("Older", Guid.Empty, reviewed, "UTC", missingReviewerId, reviewed),
                new NoShowHistoryEntry("Newest", Guid.Empty, reviewed, "UTC", reviewerId, reviewed + Duration.FromHours(1)),
            });
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());

        var result = await _controller.ViewProfile(targetId, source.Token);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<ProfileViewModel>().Subject;
        model.PageTitle.Should().Be("Target");
        model.CanViewShiftSignups.Should().BeTrue();
        model.CanViewSentMessages.Should().BeTrue();
        model.NoShowHistory!.Select(row => row.ShiftLabel).Should().Equal("Newest", "Older", "Undated");
        model.NoShowHistory!.Select(row => row.MarkedByUserId).Should().Equal(reviewerId, missingReviewerId, null);
        await _userService.DidNotReceive().GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        await _shiftManagement.Received(1).GetCoordinatorTeamIdsAsync(_viewerId, source.Token);
        await _shiftSignups.Received(1).GetNoShowHistoryAsync(targetId, source.Token);
    }

    [HumansTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ViewProfile_SharedCoordinatorReadPreservesSelfAndRoleAccess(bool ownProfile, bool privileged)
    {
        var targetId = ownProfile ? _viewerId : Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        _userService.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>())
            .Returns(BuildActiveUserInfo(_viewerId, "Viewer", "viewer@example.com"));
        _userService.GetUserInfoAsync(targetId, Arg.Any<CancellationToken>())
            .Returns(BuildActiveUserInfo(targetId, "Target", "target@example.com"));
        _shiftManagement.GetCoordinatorTeamIdsAsync(_viewerId, Arg.Any<CancellationToken>()).Returns([]);
        _shiftSignups.GetNoShowHistoryAsync(targetId, Arg.Any<CancellationToken>()).Returns([]);
        if (privileged)
        {
            ((ClaimsIdentity)_controller.User.Identity!).AddClaim(new Claim(ClaimTypes.Role, ShiftsRoles.ShiftsNoInfo));
            _authorizationService.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                ShiftsPolicies.PrivilegedSignupApprover).Returns(AuthorizationResult.Success());
        }

        var result = await _controller.ViewProfile(targetId, ct);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<ProfileViewModel>().Which;
        model.CanViewShiftSignups.Should().Be(!ownProfile && privileged);
        model.CanViewSentMessages.Should().Be(!ownProfile && privileged);
        await _shiftManagement.Received(ownProfile ? 0 : 1).GetCoordinatorTeamIdsAsync(_viewerId, ct);
        await _shiftSignups.Received(!ownProfile && privileged ? 1 : 0).GetNoShowHistoryAsync(targetId, ct);
    }

    [HumansFact]
    public async Task PublicPopover_UsesSharedCoordinatorLabel()
    {
        var id = Guid.NewGuid();
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(BuildActiveUserInfo(id, "Coordinator", "coordinator@example.com"));
        _sharedLocalizer["Profile_Coordinator"].Returns(new LocalizedString("Profile_Coordinator", "Coordinador"));
        var team = new TeamInfo(
            Guid.NewGuid(), "Infrastructure", null, "infrastructure", true, false,
            SystemTeamType.None, false, true, false, false, Instant.FromUnixTimeSeconds(0),
            [new TeamMemberInfo(Guid.NewGuid(), id, "Coordinator", null, null,
                TeamMemberRole.Coordinator, Instant.FromUnixTimeSeconds(0))]);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo> { [team.Id] = team });

        var result = await _controller.PublicPopover(id, TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<PartialViewResult>().Subject.Model
            .Should().BeOfType<PublicPopoverViewModel>().Subject;
        model.RoleLabels.Should().Equal("Coordinador · Infrastructure");
    }

    [HumansFact]
    public async Task Popover_UnknownUser_Returns404()
    {
        var id = Guid.NewGuid();
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        var result = await _controller.Popover(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Popover_UserWithoutProfile_Returns404()
    {
        var id = Guid.NewGuid();
        var user = new User { Id = id, BurnerName = "Imported Human", PreferredLanguage = "es" };
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(user, profile: null, userEmails: null));

        var result = await _controller.Popover(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
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
            State = UserState.Active,
        };
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = id,
            City = "Madrid",
            CountryCode = "ES",
        };
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(user, profile, userEmails: null));

        var result = await _controller.Popover(id, TestContext.Current.CancellationToken);

        var partial = result.Should().BeOfType<PartialViewResult>().Subject;
        partial.ViewName.Should().Be("_HumanPopover");
        var vm = partial.Model.Should().BeOfType<ProfileSummaryViewModel>().Subject;
        vm.HasProfile.Should().BeTrue();
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

        var result = await _controller.Popover(id, TestContext.Current.CancellationToken);

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
            City = "Madrid",
            CountryCode = "ES",
        };
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(user, profile, userEmails: null));

        var result = await _controller.Popover(id, TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ProfileSummaryViewModel>().Subject;
        vm.City.Should().Be("Madrid");
        vm.IsSuspended.Should().BeTrue();
    }

    [HumansFact]
    public async Task ViewProfile_SuspendedTarget_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        var user = new User { Id = id, DisplayName = "Suspended Human", State = UserState.Suspended };
        var profile = new Profile { Id = Guid.NewGuid(), UserId = id };
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(user, profile, userEmails: null));

        var result = await _controller.ViewProfile(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    private static UserInfo BuildActiveUserInfo(Guid id, string displayName, string email)
    {
        var user = new User { Id = id, BurnerName = displayName, DisplayName = displayName, State = UserState.Active, PreferredLanguage = "en" };
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = id,
            BurnerName = displayName,
        };
        var emails = new List<UserEmail>
        {
            new() { Id = Guid.NewGuid(), UserId = id, Email = email, IsVerified = true, IsPrimary = true }
        };
        return BuildUserInfo(user, profile, emails);
    }

    private static UserInfo BuildUserInfo(User user, Profile? profile, IReadOnlyList<UserEmail>? userEmails) =>
        UserInfoFactory.Create(
            user: user,
            userEmails: userEmails ?? [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: []);
}
