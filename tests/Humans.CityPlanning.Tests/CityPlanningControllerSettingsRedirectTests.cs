using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Controllers;
using Humans.CityPlanning.Data;
using Humans.CityPlanning.Services;
using Humans.Containers;
using Humans.Containers.Contracts;
using Microsoft.Extensions.Localization;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// Placement windows, scheduled times, and registration info moved to
/// /Settings#city-planning (peterdrier/Humans#1634); GeoJSON/containers/export stay on
/// <c>/CityPlanning/BarrioMap/Admin</c>, so the settings actions redirect to the tab
/// rather than back to that page.
/// </summary>
public sealed class CityPlanningControllerSettingsRedirectTests : CityPlanningTestBase
{
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly CityPlanningService _service;
    private readonly CityPlanningController _controller;
    private readonly Guid _userId = Guid.NewGuid();

    public CityPlanningControllerSettingsRedirectTests()
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, []));

        _service = new CityPlanningService(
            new CityPlanningRepository(CityPlanningDbFactory), Clock,
            Options.Create(new CityPlanningOptions { CityPlanningTeamSlug = "city-planning" }),
            _campService, Substitute.For<ITeamServiceRead>(), _userService, Substitute.For<IAuditLogService>());

        _controller = new CityPlanningController(
            _service, _campService, Substitute.For<IContainerService>(), Substitute.For<IStringLocalizer<ContainersResource>>(), _userService,
            MapAdminAuthorization(_service), NullLogger<CityPlanningController>.Instance);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
            new Claim(ClaimTypes.Role, RoleNames.CampAdmin),
        };
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
        };
        _controller.ControllerContext = new ControllerContext { HttpContext = http };
        _controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());

        var testUser = new User { Id = _userId, UserName = "test@test.com", Email = "test@test.com", DisplayName = "Test User", BurnerName = "Test User" };
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(testUser, [], [], [], profile: null, [])));
    }

    [HumansFact]
    public async Task OpenPlacement_RedirectsToTheSettingsPageCityPlanningTab()
    {
        var result = await _controller.OpenPlacement(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#city-planning");
    }

    [HumansFact]
    public async Task ClosePlacement_RedirectsToTheSettingsPageCityPlanningTab()
    {
        var result = await _controller.ClosePlacement(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#city-planning");
    }

    [HumansFact]
    public async Task UpdatePlacementDates_RedirectsToTheSettingsPageCityPlanningTab()
    {
        var result = await _controller.UpdatePlacementDates(null, null, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#city-planning");
    }

    [HumansFact]
    public async Task UpdateRegistrationInfo_SavesAndRedirectsToTheSettingsPageCityPlanningTab()
    {
        var result = await _controller.UpdateRegistrationInfo("Bring water.", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#city-planning");
        (await _service.GetRegistrationInfoAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be("Bring water.");
    }

    [HumansTheory]
    [InlineData("Index")]
    [InlineData("BarrioMap")]
    [InlineData("ContainerMap")]
    public async Task MemberMapReads_CancelTheInitialUserLookup(string page)
    {
        using var cancellation = new CancellationTokenSource();
        var user = await _userService.GetUserInfoAsync(_userId, TestContext.Current.CancellationToken);
        var canceledAtUserLookup = false;
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            var token = call.Arg<CancellationToken>();
            canceledAtUserLookup = token.IsCancellationRequested;
            token.ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(user);
        });
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns(Array.Empty<CampInfo>());

        Task<IActionResult> ReadPage() => page switch
        {
            "Index" => _controller.Index(cancellation.Token),
            "BarrioMap" => _controller.BarrioMap(cancellation.Token),
            "ContainerMap" => _controller.ContainerMap(2026, cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };

        (await ReadPage()).Should().BeOfType<ViewResult>();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(ReadPage);
        canceledAtUserLookup.Should().BeTrue("an abandoned page must stop its initial member read");
    }

}
