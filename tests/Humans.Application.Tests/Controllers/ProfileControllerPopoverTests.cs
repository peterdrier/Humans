using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.HumanLifecycle;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web;
using Humans.Web.Controllers;
using Humans.Web.Models;
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

namespace Humans.Application.Tests.Controllers;

public class ProfileControllerPopoverTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuthorizationService _authorizationService = Substitute.For<IAuthorizationService>();
    private readonly ProfileController _controller;
    private readonly Guid _viewerId = Guid.NewGuid();

    public ProfileControllerPopoverTests()
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

        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new ProfileController(
            _userService,
            userManager,
            _profileService,
            Substitute.For<IContactFieldService>(),
            Substitute.For<IEmailService>(),
            _userEmailService,
            Substitute.For<ICommunicationPreferenceService>(),
            Substitute.For<IAuditLogService>(),
            Substitute.For<IOnboardingService>(),
            Substitute.For<IHumanLifecycleService>(),
            Substitute.For<IRoleAssignmentService>(),
            Substitute.For<IShiftSignupService>(),
            Substitute.For<IShiftManagementService>(),
            Substitute.For<IShiftView>(),
            Substitute.For<IGdprExportService>(),
            Substitute.For<IConfiguration>(),
            new ConfigurationRegistry(),
            NullLogger<ProfileController>.Instance,
            localizer,
            Substitute.For<ITicketQueryService>(),
            _teamService,
            Substitute.For<ICampaignService>(),
            Substitute.For<IEmailOutboxService>(),
            new FakeClock(Instant.FromUtc(2026, 5, 9, 12, 0)),
            _authorizationService,
            Substitute.For<IConsentService>(),
            Substitute.For<IApplicationDecisionService>(),
            Substitute.For<IAccountDeletionService>(),
            Substitute.For<IMembershipCalculator>(),
            Substitute.For<IHttpClientFactory>(),
            signInManager,
            Options.Create(new GoogleWorkspaceOptions()));

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, _viewerId.ToString())
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

        var result = await _controller.Popover(id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Popover_UserWithoutProfile_RendersFallbackWithoutEmail()
    {
        // The popover is reachable by any authenticated user. Surfacing email
        // (verified or not) for the imported-no-profile path is a GDPR PII leak.
        // Admins who need email use /Profile/{id}/Admin.
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
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

        var result = await _controller.Popover(id, CancellationToken.None);

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
            IsSuspended = false,
            State = ProfileState.Active,
            City = "Madrid",
            CountryCode = "ES",
        };
        _userService.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(BuildUserInfo(user, profile, userEmails: null));
        _teamService.GetActiveTeamMembershipsForUserAsync(id, Arg.Any<CancellationToken>())
            .Returns(new List<Models.TeamMembership>());

        var result = await _controller.Popover(id, CancellationToken.None);

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

    private static UserInfo BuildUserInfo(User user, Profile? profile, IReadOnlyList<UserEmail>? userEmails) =>
        UserInfo.Create(
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
