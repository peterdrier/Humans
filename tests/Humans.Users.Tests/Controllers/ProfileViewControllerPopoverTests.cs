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
using Humans.Settings.Contracts;
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

namespace Humans.Users.Tests.Controllers;

public class ProfileViewControllerPopoverTests
{
    private readonly IUserServiceInternal _userService = Substitute.For<IUserServiceInternal>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IProfilePictureService _profilePictureService = Substitute.For<IProfilePictureService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly ITeamMessageOptionsProvider _teamMessageOptions = Substitute.For<ITeamMessageOptionsProvider>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly ICommunicationPreferenceService _commPrefService = Substitute.For<ICommunicationPreferenceService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IShiftManagementServiceRead _shiftManagement = Substitute.For<IShiftManagementServiceRead>();
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
            _emailService,
            _emailMessages,
            _commPrefService,
            _auditLogService,
            Substitute.For<IShiftSignups>(),
            Substitute.For<ISettingsService>(),
            _shiftManagement,
            localizer,
            sharedLocalizer,
            _teamService,
            _teamMessageOptions,
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

    [HumansFact]
    public async Task ViewProfile_TeamOfferedToViewer_GetsTeamSendOption()
    {
        var targetId = Guid.NewGuid();
        var option = new TeamMessageOption(Guid.NewGuid(), "Infrastructure", "infra@nobodies.team");
        var viewer = BuildActiveUserInfo(_viewerId, "Coordinator", "coordinator@example.com");
        var target = BuildActiveUserInfo(targetId, "Target", "target@example.com");
        _userService.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>()).Returns(viewer);
        _userService.GetUserInfoAsync(targetId, Arg.Any<CancellationToken>()).Returns(target);
        _commPrefService.AcceptsFacilitatedMessagesAsync(targetId, Arg.Any<CancellationToken>()).Returns(true);
        _shiftManagement.GetCoordinatorTeamIdsAsync(_viewerId).Returns([]);
        _teamMessageOptions.GetOptionsAsync(_viewerId, Arg.Any<CancellationToken>()).Returns([option]);

        var result = await _controller.ViewProfile(targetId, Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<ProfileViewModel>().Subject;
        model.TeamMessageOptions.Should().ContainSingle().Which.Should().Be(option);
    }

    [HumansFact]
    public async Task ViewProfile_RecipientOptedOut_DoesNotAskForTeamOptions()
    {
        var targetId = Guid.NewGuid();
        var viewer = BuildActiveUserInfo(_viewerId, "Coordinator", "coordinator@example.com");
        var target = BuildActiveUserInfo(targetId, "Target", "target@example.com");
        _userService.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>()).Returns(viewer);
        _userService.GetUserInfoAsync(targetId, Arg.Any<CancellationToken>()).Returns(target);
        _commPrefService.AcceptsFacilitatedMessagesAsync(targetId, Arg.Any<CancellationToken>()).Returns(false);
        _shiftManagement.GetCoordinatorTeamIdsAsync(_viewerId).Returns([]);

        var result = await _controller.ViewProfile(targetId, Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<ProfileViewModel>().Subject;
        model.TeamMessageOptions.Should().BeEmpty();
        await _teamMessageOptions.DidNotReceiveWithAnyArgs().GetOptionsAsync(default, default);
    }

    [HumansFact]
    public async Task SendMessageGet_TeamNotOfferedToViewer_IsForbidden()
    {
        var targetId = Guid.NewGuid();
        var viewer = BuildActiveUserInfo(_viewerId, "Viewer", "viewer@example.com");
        var target = BuildActiveUserInfo(targetId, "Target", "target@example.com");
        _userService.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>()).Returns(viewer);
        _userService.GetUserInfoAsync(targetId, Arg.Any<CancellationToken>()).Returns(target);
        _commPrefService.AcceptsFacilitatedMessagesAsync(targetId, Arg.Any<CancellationToken>()).Returns(true);
        _teamMessageOptions.GetOptionsAsync(_viewerId, Arg.Any<CancellationToken>()).Returns([]);

        var result = await _controller.SendMessage(targetId, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task SendMessagePost_TeamNotOfferedToViewer_IsForbiddenAndDoesNotSend()
    {
        var targetId = Guid.NewGuid();
        var otherTeam = new TeamMessageOption(Guid.NewGuid(), "Other", "other@nobodies.team");
        var viewer = BuildActiveUserInfo(_viewerId, "Viewer", "viewer@example.com");
        var target = BuildActiveUserInfo(targetId, "Target", "target@example.com");
        _userService.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>()).Returns(viewer);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [_viewerId] = viewer, [targetId] = target });
        _commPrefService.AcceptsFacilitatedMessagesAsync(targetId, Arg.Any<CancellationToken>()).Returns(true);
        _teamMessageOptions.GetOptionsAsync(_viewerId, Arg.Any<CancellationToken>()).Returns([otherTeam]);

        var result = await _controller.SendMessage(targetId, new SendMessageViewModel
        {
            Message = "Hello",
            SendAsTeamId = Guid.NewGuid()
        }, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
    }

    [HumansFact]
    public async Task SendMessagePost_TeamOfferedToViewer_UsesGroupReplyToAndAudits()
    {
        var targetId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var viewer = BuildActiveUserInfo(_viewerId, "Coordinator", "coordinator@example.com");
        var target = BuildActiveUserInfo(targetId, "Target", "target@example.com");
        _userService.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>()).Returns(viewer);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [_viewerId] = viewer, [targetId] = target });
        _commPrefService.AcceptsFacilitatedMessagesAsync(targetId, Arg.Any<CancellationToken>()).Returns(true);
        _teamMessageOptions.GetOptionsAsync(_viewerId, Arg.Any<CancellationToken>())
            .Returns([new TeamMessageOption(teamId, "Infrastructure", "infra@nobodies.team")]);
        _emailMessages.FacilitatedMessage(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new EmailMessage("target@example.com", "Target", "Subject", "Body", "facilitated_message"));

        var result = await _controller.SendMessage(targetId, new SendMessageViewModel
        {
            Message = "Hello",
            SendAsTeamId = teamId
        }, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>();
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.ReplyTo == "infra@nobodies.team"),
            Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.FacilitatedMessageSent,
            nameof(User),
            targetId,
            Arg.Is<string>(d => d.Contains("from team Infrastructure", StringComparison.Ordinal)
                && !d.Contains("infra@nobodies.team", StringComparison.Ordinal)),
            _viewerId,
            teamId,
            "Team");
    }

    private static UserInfo BuildActiveUserInfo(Guid id, string displayName, string email)
    {
        var user = new User { Id = id, DisplayName = displayName, State = UserState.Active, PreferredLanguage = "en" };
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = id,
            BurnerName = displayName,
            MembershipTier = MembershipTier.Volunteer,
            IsApproved = true
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
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
}
