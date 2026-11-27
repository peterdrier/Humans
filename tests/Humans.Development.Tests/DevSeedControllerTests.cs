using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Development.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.Development.Tests;

public class DevSeedControllerTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IWebHostEnvironment _environment = Substitute.For<IWebHostEnvironment>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ConfigurationRegistry _configRegistry = new();
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();

    [HumansTheory]
    [Xunit.InlineData("Production", true)]
    [Xunit.InlineData("Staging", false)]
    public void Index_DevSeedGateClosed_ReturnsNotFound(string environment, bool devAuthEnabled)
    {
        _environment.EnvironmentName.Returns(environment);
        SetDevAuthEnabled(devAuthEnabled);

        BuildSut().Index().Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public void Index_DevSeedGateOpen_RendersSeedPage()
    {
        // Staging with DevAuth on is QA / a preview.
        _environment.EnvironmentName.Returns("Staging");
        SetDevAuthEnabled(true);
        _serviceProvider.GetService(typeof(Services.Demo.DemoRunStatus))
            .Returns(new Services.Demo.DemoRunStatus());
        var ctrl = BuildSut();
        ctrl.TempData = Substitute.For<ITempDataDictionary>();

        ctrl.Index().Should().BeOfType<ViewResult>();
    }

    public enum DevSeedAction
    {
        SeedBudget,
        SeedCampRoles
    }

    [HumansTheory]
    [Xunit.InlineData(DevSeedAction.SeedBudget)]
    [Xunit.InlineData(DevSeedAction.SeedCampRoles)]
    public async Task Seed_PreflightCancelled_ResolvesNoSeeder(DevSeedAction action)
    {
        _environment.EnvironmentName.Returns("Development");
        SetDevAuthEnabled(true);
        var controller = BuildSut();
        var userId = Guid.Parse(controller.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        using var request = new CancellationTokenSource();
        controller.HttpContext.RequestAborted = request.Token;
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Should().Be(request.Token);
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = userId }, [], [], null));
        });
        await request.CancelAsync();
        Func<Task<IActionResult>> invoke = action switch
        {
            DevSeedAction.SeedBudget => () => controller.SeedBudget(request.Token),
            _ => () => controller.SeedCampRoles(request.Token),
        };

        await invoke.Should().ThrowAsync<OperationCanceledException>();
        _serviceProvider.ReceivedCalls().Should().BeEmpty();
    }

    private void SetDevAuthEnabled(bool enabled)
    {
        // IConfiguration["DevAuth:Enabled"] is consulted by GetValue<bool>.
        var section = Substitute.For<IConfigurationSection>();
        section.Value.Returns(enabled ? "true" : "false");
        section.Path.Returns("DevAuth:Enabled");
        section.Key.Returns("Enabled");
        _configuration.GetSection("DevAuth:Enabled").Returns(section);
        _configuration["DevAuth:Enabled"].Returns(enabled ? "true" : "false");
    }

    private DevSeedController BuildSut()
    {
        var ctrl = new DevSeedController(
            _environment,
            _configuration,
            _configRegistry,
            _serviceProvider,
            Substitute.For<Hangfire.IBackgroundJobClient>(),
            _userService,
            NullLogger<DevSeedController>.Instance);

        // Resolve ILoggerFactory from RequestServices for SetError (unused on
        // happy path here, but required if the gating path is ever extended).
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
                ],
                "test")),
            RequestServices = services.BuildServiceProvider(),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }
}
