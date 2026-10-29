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
using Microsoft.AspNetCore.Authorization;
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
            _campService, Substitute.For<ITeamService>(), _userService, Substitute.For<IAuditLogService>(), ActiveEvent(PublicYear));

        var testUser = new User { Id = _userId, UserName = "test@test.com", Email = "test@test.com", DisplayName = "Test User", BurnerName = "Test User" };
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(testUser, [], [], [], profile: null, [])));
    }

    [HumansFact]
    public async Task CreateBarrioContainer_RedirectsToTheRouteYear()
    {
        _containerService.CreateAsync(Arg.Any<ContainerData>(), _userId, Arg.Any<CancellationToken>())
            .Returns(new ContainerMutationResult<ContainerDto>(ContainerDtoFor(Guid.NewGuid())));
        var controller = CreateController(CampsRoles.CampsAdmin);

        var result = await controller.CreateBarrioContainer(
            RouteYear, Guid.NewGuid(), new ContainerFormModel { Name = "Box" }, TestContext.Current.CancellationToken);

        AssertRedirectsToContainers(result, RouteYear);
    }

    [HumansFact]
    public async Task CreateBarrioContainer_InvalidModel_RedirectsToTheRouteYear()
    {
        var controller = CreateController(CampsRoles.CampsAdmin);
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
        var controller = CreateController(CampsRoles.CampsAdmin);

        var result = await controller.EditContainer(
            RouteYear, id, new ContainerFormModel { Name = "Box" }, TestContext.Current.CancellationToken);

        AssertRedirectsToContainers(result, RouteYear);
    }

    [HumansFact]
    public async Task EditContainer_InvalidModel_RedirectsToTheRouteYear()
    {
        var id = Guid.NewGuid();
        _containerService.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(ContainerDtoFor(id));
        var controller = CreateController(CampsRoles.CampsAdmin);
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
        var controller = CreateController(CampsRoles.CampsAdmin);

        var result = await controller.DeleteContainer(RouteYear, id, TestContext.Current.CancellationToken);

        AssertRedirectsToContainers(result, RouteYear);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContainerMap_LeadIsGatedByThePlaceRequirementForTheirCampInTheRouteYear(bool placeAllowed)
    {
        var campId = SeedLeadCamp();
        object? target = null;
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(call =>
            {
                target = call.ArgAt<object?>(1);
                return call.ArgAt<IEnumerable<IAuthorizationRequirement>>(2).Single() == ContainerOperationRequirement.Place
                    && placeAllowed ? AuthorizationResult.Success() : AuthorizationResult.Failed();
            });
        var controller = CreateController(authorization);

        var result = await controller.ContainerMap(RouteYear, TestContext.Current.CancellationToken);

        if (placeAllowed) result.Should().BeOfType<ViewResult>();
        else result.Should().BeOfType<ForbidResult>();
        target.Should().Be(new ContainerAuthorizationTarget(campId) { Year = RouteYear });
    }

    [HumansFact]
    public async Task ContainerMap_NonLeadIsRefusedWithoutCreatingASettingsRow()
    {
        _campService.GetCampsForYearAsync(RouteYear, Arg.Any<CancellationToken>()).Returns([]);
        var controller = CreateController();

        var result = await controller.ContainerMap(RouteYear, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
        CityPlanningDb.CityPlanningSettings.Should().BeEmpty();
    }

    /// <summary>
    /// The map's base layers come from the state API, which 404s with no active event — so the page
    /// itself 404s instead of rendering a map whose state fetch fails, even for a map admin.
    /// </summary>
    [HumansFact]
    public async Task ContainerMap_NoActiveEvent_IsNotFoundEvenForAMapAdmin()
    {
        var service = new CityPlanningService(
            new CityPlanningRepository(CityPlanningDbFactory), Clock,
            Options.Create(new CityPlanningOptions { CityPlanningTeamSlug = "city-planning" }),
            _campService, Substitute.For<ITeamService>(), _userService, Substitute.For<IAuditLogService>(), ActiveEvent(null));
        var controller = CreateController(service, MapAdminAuthorization(service), CampsRoles.CampsAdmin);

        var result = await controller.ContainerMap(RouteYear, TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
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

    private Guid SeedLeadCamp()
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
        return campId;
    }

    private CityPlanningController CreateController(params string[] roles) => CreateController(null, roles);

    private CityPlanningController CreateController(IAuthorizationService? authorization, params string[] roles) =>
        CreateController(_service, authorization ?? MapAdminAuthorization(_service), roles);

    private CityPlanningController CreateController(
        CityPlanningService service, IAuthorizationService authorization, params string[] roles)
    {
        var controller = new CityPlanningController(
            service, _campService, _containerService, Substitute.For<IStringLocalizer<ContainersResource>>(), _userService,
            authorization, NullLogger<CityPlanningController>.Instance);

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
