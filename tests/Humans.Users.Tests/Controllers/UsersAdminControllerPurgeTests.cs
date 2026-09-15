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
    private readonly IWebHostEnvironment _environment = Substitute.For<IWebHostEnvironment>();
    private readonly Guid _adminUserId = Guid.NewGuid();

    public UsersAdminControllerPurgeTests()
    {
        _environment.EnvironmentName.Returns("Development");
        _userService.GetUserInfoAsync(_adminUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(new User { Id = _adminUserId, PreferredLanguage = "en" }.ToUserInfo()));
    }

    private UsersAdminController BuildController()
    {
        var c = new UsersAdminController(
            _userService,
            Substitute.For<IUserEmailService>(),
            Substitute.For<IEmailOutboxServiceRead>(),
            Substitute.For<IRoleAssignmentService>(),
            Substitute.For<IApplicationServiceRead>(),
            Substitute.For<IConsentServiceRead>(),
            Substitute.For<ICampaignServiceRead>(),
            Substitute.For<IHumanLifecycleService>(),
            Substitute.For<IOnboardingIntake>(),
            Substitute.For<IAuditLogService>(),
            Substitute.For<IUsersAudienceService>(),
            _deletion,
            _environment,
            Substitute.For<IClock>(),
            Substitute.For<IAuthorizationService>(),
            NullLogger<UsersAdminController>.Instance,
            Substitute.For<IStringLocalizer<SharedResource>>());

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
        _userService.GetUserInfoAsync(target, Arg.Any<CancellationToken>())
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
}
