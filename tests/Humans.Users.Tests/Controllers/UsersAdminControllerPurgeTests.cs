using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Base;
using Humans.Campaigns.Contracts;
using Humans.Consent.Contracts;
using Humans.Email.Contracts;
using Humans.Governance.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Controllers;
using Humans.Users.Models;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Users.Tests.Controllers;

/// <summary>
/// Purge never runs in Production and never on oneself.
/// </summary>
public class UsersAdminControllerPurgeTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IAccountDeletionService _deletion = Substitute.For<IAccountDeletionService>();
    private readonly IHumanLifecycleService _lifecycle = Substitute.For<IHumanLifecycleService>();
    private readonly IOnboardingIntake _onboarding = Substitute.For<IOnboardingIntake>();
    private readonly IRoleAssignmentService _roleAssignments = Substitute.For<IRoleAssignmentService>();
    private readonly IUsersAudienceService _audience = Substitute.For<IUsersAudienceService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly IWebHostEnvironment _environment = Substitute.For<IWebHostEnvironment>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IStringLocalizer<SharedResource> _localizer = Substitute.For<IStringLocalizer<SharedResource>>();
    private readonly Guid _adminUserId = Guid.NewGuid();

    public UsersAdminControllerPurgeTests()
    {
        _environment.EnvironmentName.Returns("Development");
        _userService.GetUserInfoAsync(_adminUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(new User { Id = _adminUserId, PreferredLanguage = "en" }.ToUserInfo()));
        _localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key, key);
        });
        _userService.GetRawUserInfoAsync(_adminUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(new User { Id = _adminUserId, PreferredLanguage = "en" }.ToUserInfo()));
    }

    private UsersAdminController BuildController()
    {
        var c = new UsersAdminController(
            _userService,
            Substitute.For<IUserEmailService>(),
            Substitute.For<IEmailOutboxServiceRead>(),
            _roleAssignments,
            Substitute.For<IApplicationServiceRead>(),
            Substitute.For<IConsentServiceRead>(),
            Substitute.For<ICampaignServiceRead>(),
            _lifecycle,
            _onboarding,
            _auditLog,
            _audience,
            _deletion,
            _environment,
            _clock,
            Substitute.For<IAuthorizationService>(),
            NullLogger<UsersAdminController>.Instance,
            _localizer);

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, _adminUserId.ToString())
        ], authenticationType: "TestAuth");
        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = services.BuildServiceProvider(),
        };
        c.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Test" },
            RouteData = new RouteData(),
        };
        c.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());
        c.Url = Substitute.For<IUrlHelper>();
        return c;
    }

    [HumansFact]
    public async Task PurgeHuman_InProduction_ReturnsNotFoundWithoutPurging()
    {
        _environment.EnvironmentName.Returns("Production");
        var target = Guid.NewGuid();
        _userService.GetRawUserInfoAsync(target, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(new User { Id = target }.ToUserInfo()));

        var result = await BuildController().PurgeHuman(target);

        result.Should().BeOfType<NotFoundResult>();
        await _deletion.DidNotReceive().PurgeAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PurgeHuman_TargetIsSelf_RedirectsToDetailWithoutPurging()
    {
        var result = await BuildController().PurgeHuman(_adminUserId);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminController.AdminDetail));
        await _deletion.DidNotReceive().PurgeAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SuspendHuman_ForwardsActorAndNotesThenRedirectsToDetail()
    {
        var target = Guid.NewGuid();
        _lifecycle.SuspendAsync(target, _adminUserId, "Repeatedly misses shifts", Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var result = await BuildController().SuspendHuman(target, "Repeatedly misses shifts");

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminController.AdminDetail));
        await _lifecycle.Received(1).SuspendAsync(target, _adminUserId, "Repeatedly misses shifts", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UnsuspendHuman_ForwardsActorThenRedirectsToDetail()
    {
        var target = Guid.NewGuid();
        _lifecycle.UnsuspendAsync(target, _adminUserId, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var result = await BuildController().UnsuspendHuman(target);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminController.AdminDetail));
        await _lifecycle.Received(1).UnsuspendAsync(target, _adminUserId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RejectSignup_ForwardsActorAndReasonThenRedirectsToDetail()
    {
        var target = Guid.NewGuid();
        _onboarding.RejectSignupAsync(target, _adminUserId, "Duplicate signup", Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var result = await BuildController().RejectSignup(target, "Duplicate signup");

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminController.AdminDetail));
        await _onboarding.Received(1).RejectSignupAsync(target, _adminUserId, "Duplicate signup", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Roles_ForwardsFilterAndBuildsTheSharedRolesView()
    {
        var now = Instant.FromUtc(2026, 9, 21, 6, 0);
        _clock.GetCurrentInstant().Returns(now);
        _roleAssignments.GetFilteredAsync("Board", activeOnly: false, page: 2, pageSize: 50, now, Arg.Any<CancellationToken>())
            .Returns((Array.Empty<RoleAssignmentSummarySnapshot>(), 0));

        var result = await BuildController().Roles("Board", showInactive: true, page: 2);

        result.Should().BeOfType<ViewResult>()
            .Which.ViewName.Should().Be("~/Views/Shared/Roles.cshtml");
        await _roleAssignments.Received(1).GetFilteredAsync("Board", activeOnly: false, page: 2, pageSize: 50, now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Audience_ProjectsSegmentationForTheSelectedYear()
    {
        var segmentation = new AudienceSegmentation(10, 6, 8, 5, 1, [2025, 2026], 2026);
        _audience.GetAudienceSegmentationAsync(2026, Arg.Any<CancellationToken>()).Returns(segmentation);

        var result = await BuildController().Audience(2026, CancellationToken.None);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        var model = view.Model.Should().BeOfType<AudienceSegmentationViewModel>().Subject;
        model.TotalAccounts.Should().Be(10);
        model.WithTicket.Should().Be(6);
        model.WithProfile.Should().Be(8);
        model.WithBoth.Should().Be(5);
        model.WithNeither.Should().Be(1);
        model.AvailableYears.Should().Equal(2025, 2026);
        model.SelectedYear.Should().Be(2026);
    }

    [HumansFact]
    public async Task RevealIban_StoresTheIbanAndAuditsTheAdminAction()
    {
        var target = Guid.NewGuid();
        var user = new User { Id = target, PreferredLanguage = "en" };
        var profile = new Profile { Id = Guid.NewGuid(), UserId = target, Iban = "ES91 2100 0418 4502 0005 1332" };
        var targetInfo = UserInfoFactory.Create(user, [], [], [], profile, [], [], [], []);
        _userService.GetRawUserInfoAsync(target, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(targetInfo));

        var controller = BuildController();
        var result = await controller.RevealIban(target, CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(UsersAdminController.AdminDetail));
        controller.TempData["RevealedIban"].Should().Be(profile.Iban);
        await _auditLog.Received(1).LogAsync(
            AuditAction.IbanReveal, nameof(User), target,
            Arg.Is<string>(message => message.Contains(target.ToString(), StringComparison.Ordinal)),
            _adminUserId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }
}
