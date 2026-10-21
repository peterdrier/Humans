using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Controllers;
using Humans.CityPlanning.Data;
using Humans.CityPlanning.Domain;
using Humans.CityPlanning.Services;
using Humans.Containers;
using Humans.Containers.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// Container admin follows the route year, not the public year (CITY-1, CITY-6): the public year
/// is 2026 throughout, so any answer that mirrors 2026 instead of the route's 2025 is the bug.
/// </summary>
public sealed class CityPlanningContainerAdminYearTests : CityPlanningTestBase
{
    private const int PublicYear = 2026;
    private const int RouteYear = 2025;

    private readonly ICampService _campService = Substitute.For<ICampService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IContainerService _containerService = Substitute.For<IContainerService>();
    private readonly CityPlanningService _service;
    private readonly Guid _userId = Guid.NewGuid();

    public CityPlanningContainerAdminYearTests()
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(PublicYear, []));

        _service = new CityPlanningService(
            new CityPlanningRepository(CityPlanningDbFactory), Clock,
            Options.Create(new CityPlanningOptions { CityPlanningTeamSlug = "city-planning" }),
            _campService, Substitute.For<ITeamService>(), _userService, Substitute.For<IAuditLogService>());

        var testUser = new User { Id = _userId, UserName = "test@test.com", Email = "test@test.com", DisplayName = "Test User", BurnerName = "Test User" };
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(testUser, [], [], [], profile: null, [])));
    }

    [HumansFact]
    public async Task CreateBarrioContainer_RedirectsToTheRouteYear()
    {
        _containerService.CreateAsync(Arg.Any<ContainerData>(), _userId, Arg.Any<CancellationToken>())
            .Returns(new ContainerMutationResult<ContainerDto>(ContainerDtoFor(Guid.NewGuid())));
        var controller = CreateController(RoleNames.CampAdmin);

        var result = await controller.CreateBarrioContainer(
            RouteYear, Guid.NewGuid(), new ContainerFormModel { Name = "Box" }, TestContext.Current.CancellationToken);

        AssertRedirectsToContainers(result, RouteYear);
    }

    [HumansFact]
    public async Task CreateBarrioContainer_InvalidModel_RedirectsToTheRouteYear()
    {
        var controller = CreateController(RoleNames.CampAdmin);
        controller.ModelState.AddModelError("Name", "Required");

        var result = await controller.CreateBarrioContainer(
            RouteYear, Guid.NewGuid(), new ContainerFormModel(), TestContext.Current.CancellationToken);

        AssertRedirectsToContainers(result, RouteYear);
    }

    [HumansFact]
    public async Task EditContainer_RedirectsToTheRouteYear()
    {
        var id = Guid.NewGuid();
        _containerService.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(ContainerDtoFor(id));
        _containerService.UpdateAsync(id, Arg.Any<ContainerData>(), _userId, Arg.Any<CancellationToken>())
            .Returns(new ContainerMutationResult<ContainerDto>(ContainerDtoFor(Guid.NewGuid())));
        var controller = CreateController(RoleNames.CampAdmin);

        var result = await controller.EditContainer(
            RouteYear, id, new ContainerFormModel { Name = "Box" }, TestContext.Current.CancellationToken);

        AssertRedirectsToContainers(result, RouteYear);
    }

    [HumansFact]
    public async Task EditContainer_InvalidModel_RedirectsToTheRouteYear()
    {
        var id = Guid.NewGuid();
        _containerService.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(ContainerDtoFor(id));
        var controller = CreateController(RoleNames.CampAdmin);
        controller.ModelState.AddModelError("Name", "Required");

        var result = await controller.EditContainer(
            RouteYear, id, new ContainerFormModel(), TestContext.Current.CancellationToken);

        AssertRedirectsToContainers(result, RouteYear);
    }

    [HumansFact]
    public async Task DeleteContainer_RedirectsToTheRouteYear()
    {
        var id = Guid.NewGuid();
        _containerService.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(ContainerDtoFor(id));
        var controller = CreateController(RoleNames.CampAdmin);

        var result = await controller.DeleteContainer(RouteYear, id, TestContext.Current.CancellationToken);

        AssertRedirectsToContainers(result, RouteYear);
    }

    [HumansFact]
    public async Task ContainerMap_LeadIsAdmittedByTheRouteYearsPhase_NotThePublicYears()
    {
        await SeedPlacementAsync(RouteYear, open: true);
        await SeedPlacementAsync(PublicYear, open: false);
        SeedLeadCamp();
        var controller = CreateController();

        var result = await controller.ContainerMap(RouteYear, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>();
    }

    [HumansFact]
    public async Task ContainerMap_LeadIsRefusedWhenTheRouteYearsPhaseIsClosed_EvenIfThePublicYearIsOpen()
    {
        await SeedPlacementAsync(RouteYear, open: false);
        await SeedPlacementAsync(PublicYear, open: true);
        SeedLeadCamp();
        var controller = CreateController();

        var result = await controller.ContainerMap(RouteYear, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
    }

    private static void AssertRedirectsToContainers(IActionResult result, int year)
    {
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(CityPlanningController.Containers));
        redirect.RouteValues.Should().NotBeNull();
        redirect.RouteValues!["year"].Should().Be(year);
    }

    private static ContainerDto ContainerDtoFor(Guid id) =>
        new(id, Guid.NewGuid(), "Box", null, [], Instant.MinValue, Instant.MinValue);

    private async Task SeedPlacementAsync(int year, bool open)
    {
        CityPlanningDb.CityPlanningSettings.Add(new CityPlanningSettings
        {
            Year = year,
            IsContainerPlacementOpen = open,
            UpdatedAt = Clock.GetCurrentInstant(),
        });
        await CityPlanningDb.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private void SeedLeadCamp()
    {
        var campId = Guid.NewGuid();
        var season = new CampSeasonInfo(
            Guid.NewGuid(), campId, string.Empty, RouteYear, null, "Lead Camp", string.Empty, string.Empty,
            [], CampSeasonStatus.Pending, YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
            0, null, null, null, 0, null, null)
        {
            LeadUserIds = [_userId],
        };
        _campService.GetCampsForYearAsync(RouteYear, Arg.Any<CancellationToken>())
            .Returns([new CampInfo(campId, "lead-camp", string.Empty, string.Empty, false, 0, [season])]);
    }

    private CityPlanningController CreateController(params string[] roles)
    {
        var controller = new CityPlanningController(
            _service, _campService, _containerService, Substitute.For<IStringLocalizer<ContainersResource>>(), _userService,
            MapAdminAuthorization(_service), NullLogger<CityPlanningController>.Instance);

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, _userId.ToString()) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }
}
