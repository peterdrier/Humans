using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Covers <c>Shifts.md</c> invariant line 239:
/// "DevelopmentDashboardSeeder and its POST /dev/seed/dashboard endpoint are
/// gated to IWebHostEnvironment.IsDevelopment() AND the DevAuth:Enabled setting.
/// QA, preview, and production environments cannot invoke it regardless of role."
///
/// The role policy (<c>ShiftDashboardAccess</c>) is enforced by the
/// <c>[Authorize]</c> attribute and tested elsewhere; this file exercises the
/// in-action environment / config gating that runs even after the policy passes.
/// </summary>
public class DevSeedControllerTests
{
    private readonly UserManager<User> _userManager;
    private readonly IWebHostEnvironment _environment = Substitute.For<IWebHostEnvironment>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ConfigurationRegistry _configRegistry = new();
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();

    public DevSeedControllerTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
    }

    [HumansFact]
    public async Task SeedDashboard_NotDevelopmentEnvironment_ReturnsNotFound()
    {
        // Arrange: ASPNETCORE_ENVIRONMENT != Development (e.g. QA / preview / prod).
        // Even if DevAuth:Enabled were true, the stricter IsDevelopment() gate must
        // refuse the seed.
        _environment.EnvironmentName.Returns("Production");
        SetDevAuthEnabled(true);
        var ctrl = BuildSut();

        // Act
        var result = await ctrl.SeedDashboard(CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
        // Seeder must not be resolved when gating fails — verifies the early-out
        // happened before any DI lookup of the seeder dependency.
        _serviceProvider.DidNotReceive().GetService(Arg.Any<Type>());
    }

    [HumansFact]
    public async Task SeedDashboard_DevelopmentButDevAuthDisabled_ReturnsNotFound()
    {
        // Arrange: ASPNETCORE_ENVIRONMENT == Development but DevAuth:Enabled == false.
        // The seed endpoint requires BOTH gates — config alone gates QA/preview.
        _environment.EnvironmentName.Returns("Development");
        SetDevAuthEnabled(false);
        var ctrl = BuildSut();

        // Act
        var result = await ctrl.SeedDashboard(CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
        _serviceProvider.DidNotReceive().GetService(Arg.Any<Type>());
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
            _userManager,
            NullLogger<DevSeedController>.Instance);

        // Resolve ILoggerFactory from RequestServices for SetError (unused on
        // happy path here, but required if the gating path is ever extended).
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) },
                "test")),
            RequestServices = services.BuildServiceProvider(),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }
}
