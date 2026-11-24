using Humans.Containers.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Data;
using Humans.CityPlanning.Services;
using Humans.CityPlanning.ViewComponents;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Humans.CityPlanning.Tests;

/// <summary>The placement box City Planning adds to a camp's page: its leads and map admins only.</summary>
public sealed class CampPlacementPhaseViewComponentTests : CityPlanningTestBase
{
    private static readonly Guid CampId = Guid.NewGuid();
    private static readonly Guid ViewerId = Guid.NewGuid();

    private readonly ICampService _camps = Substitute.For<ICampService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    [HumansTheory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task ShowsOnlyToMapAdminsAndTheCampsLeads(bool mapAdmin, bool lead, bool shown)
    {
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), PolicyNames.CityPlanningMapAdmin)
            .Returns(mapAdmin ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        _users.GetUserInfoAsync(ViewerId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = ViewerId }, [], [], profile: null));
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([Camp(leadUserIds: lead ? [ViewerId] : [])]);

        var result = await BuildSut(ActiveEvent()).InvokeAsync(CampId);

        if (shown) result.Should().BeOfType<ViewViewComponentResult>();
        else result.Should().BeOfType<ContentViewComponentResult>();
    }

    [HumansFact]
    public async Task RendersNothingWithoutAnActiveEvent()
    {
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), PolicyNames.CityPlanningMapAdmin)
            .Returns(AuthorizationResult.Success());

        var result = await BuildSut(ActiveEvent(null)).InvokeAsync(CampId);

        result.Should().BeOfType<ContentViewComponentResult>();
    }

    private CampPlacementPhaseViewComponent BuildSut(Humans.Settings.Contracts.ISettingsService settings)
    {
        var service = new CityPlanningService(
            new CityPlanningRepository(CityPlanningDbFactory), Clock,
            Options.Create(new CityPlanningOptions { CityPlanningTeamSlug = "city-planning" }),
            _camps, Substitute.For<ITeamService>(), _users, Substitute.For<IAuditLogService>(), settings, Substitute.For<IContainerService>());
        var component = new CampPlacementPhaseViewComponent(service, _users, _authorization);
        component.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString())], "test")),
                },
                ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            },
        };
        return component;
    }

    private static CampInfo Camp(IReadOnlyList<Guid> leadUserIds) =>
        new(CampId, "camp", string.Empty, string.Empty, false, 0,
        [
            new CampSeasonInfo(Guid.NewGuid(), CampId, 2026, null, "Camp", string.Empty, string.Empty,
                [], CampSeasonStatus.Active, YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
                0, null, null, null, 0, null, null) { LeadUserIds = leadUserIds },
        ]);
}
