using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
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
using Humans.Testing;
using Humans.Web;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
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
            Substitute.For<IGdprExportService>(),
            Substitute.For<IConfiguration>(),
            new ConfigurationRegistry(),
            NullLogger<ProfileController>.Instance,
            localizer,
            Substitute.For<ITicketQueryService>(),
            _teamService,
            Substitute.For<ICampaignService>(),
            Substitute.For<IEmailOutboxService>(),
            new MemoryCache(new MemoryCacheOptions()),
            new FakeClock(Instant.FromUtc(2026, 5, 9, 12, 0)),
            Substitute.For<IAuthorizationService>(),
            _userService,
            Substitute.For<IConsentService>(),
            Substitute.For<IApplicationDecisionService>(),
            Substitute.For<IAccountDeletionService>(),
            Substitute.For<IMembershipCalculator>(),
            Substitute.For<IHttpClientFactory>(),
            signInManager,
            Options.Create(new GoogleWorkspaceOptions()));

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _viewerId.ToString()),
        }, authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());
    }

    [HumansFact]
    public async Task Popover_UnknownUser_Returns404()
    {
        var id = Guid.NewGuid();
        _userService.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _controller.Popover(id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Popover_UserWithoutProfile_RendersFallbackWithVerifiedPrimaryEmail()
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            DisplayName = "Imported Human",
            Email = null,
            PreferredLanguage = "es",
            ProfilePictureUrl = null,
        };
        _userService.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(user);
        _profileService.GetProfileAsync(id, Arg.Any<CancellationToken>()).Returns((Profile?)null);

        var emails = new List<UserEmailEditDto>
        {
            new(Guid.NewGuid(), "secondary@example.com", IsVerified: true, IsGoogle: false,
                Provider: null, ProviderKey: null, IsPrimary: false, Visibility: null,
                IsPendingVerification: false),
            new(Guid.NewGuid(), "primary@example.com", IsVerified: true, IsGoogle: false,
                Provider: null, ProviderKey: null, IsPrimary: true, Visibility: null,
                IsPendingVerification: false),
        };
        _userEmailService.GetUserEmailsAsync(id, Arg.Any<CancellationToken>()).Returns(emails);

        var result = await _controller.Popover(id, CancellationToken.None);

        var partial = result.Should().BeOfType<PartialViewResult>().Subject;
        partial.ViewName.Should().Be("_HumanPopover");
        var vm = partial.Model.Should().BeOfType<ProfileSummaryViewModel>().Subject;
        vm.HasProfile.Should().BeFalse();
        vm.DisplayName.Should().Be("Imported Human");
        vm.Email.Should().Be("primary@example.com");
        vm.PreferredLanguage.Should().Be("es");
        vm.MembershipTier.Should().BeNull();
        vm.MembershipStatus.Should().BeNull();
        vm.City.Should().BeNull();
        vm.Teams.Should().BeEmpty();
        vm.Languages.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Popover_UserWithoutProfile_DoesNotSeedFromLegacyUserEmail()
    {
        // Regression: User.Email falls back to the legacy Identity column when
        // UserEmails isn't loaded (see User.cs SILENT-FALLBACK FOOTGUN), so the
        // fallback popover must always derive from verified UserEmail rows
        // regardless of what popoverUser.Email returns.
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            DisplayName = "Imported Human",
            Email = "stale-legacy@example.com",
            PreferredLanguage = "en",
        };
        _userService.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(user);
        _profileService.GetProfileAsync(id, Arg.Any<CancellationToken>()).Returns((Profile?)null);

        var emails = new List<UserEmailEditDto>
        {
            new(Guid.NewGuid(), "canonical@example.com", IsVerified: true, IsGoogle: false,
                Provider: null, ProviderKey: null, IsPrimary: true, Visibility: null,
                IsPendingVerification: false),
        };
        _userEmailService.GetUserEmailsAsync(id, Arg.Any<CancellationToken>()).Returns(emails);

        var result = await _controller.Popover(id, CancellationToken.None);

        var partial = result.Should().BeOfType<PartialViewResult>().Subject;
        var vm = partial.Model.Should().BeOfType<ProfileSummaryViewModel>().Subject;
        vm.Email.Should().Be("canonical@example.com");
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
        _userService.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(user);

        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = id,
            MembershipTier = MembershipTier.Volunteer,
            IsApproved = true,
            IsSuspended = false,
            City = "Madrid",
            CountryCode = "ES",
        };
        _profileService.GetProfileAsync(id, Arg.Any<CancellationToken>()).Returns(profile);
        _profileService.GetProfileLanguagesAsync(profile.Id, Arg.Any<CancellationToken>())
            .Returns(new List<ProfileLanguage>());
        _teamService.GetActiveTeamMembershipsForUserAsync(id, Arg.Any<CancellationToken>())
            .Returns(new List<Humans.Application.Models.TeamMembership>());

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
}
