using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Authorization;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Controllers;
using Humans.GoogleIntegration.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// The team Google-resources page (<c>/Google/Teams/{slug}/Resources</c>): every action resolves
/// the team by slug (404), gates on TeamsAdmin/Board/Admin or the department's resource managers
/// (403), and every per-resource action rejects a resource from another team (404).
/// </summary>
public class TeamResourcesControllerTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly ITeamResourceManagement _resources = Substitute.For<ITeamResourceManagement>();
    private readonly IGoogleSyncManagement _sync = Substitute.For<IGoogleSyncManagement>();
    private readonly TeamInfo _team = Team(Guid.NewGuid(), parentTeamId: null);

    public TeamResourcesControllerTests()
    {
        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(
            new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = _userId }, [], [], [], null, [])));
        _teams.GetTeamBySlugAsync(_team.Slug, Arg.Any<CancellationToken>()).Returns(_team);
        _resources.GetTeamResourcesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        _resources.GetServiceAccountEmailAsync(Arg.Any<CancellationToken>()).Returns("service@example.com");
    }

    [HumansTheory]
    [InlineData("permission", "foreign")]
    [InlineData("permission", "missing")]
    [InlineData("permission", "own")]
    [InlineData("permission", "denied")]
    [InlineData("restriction", "foreign")]
    [InlineData("restriction", "missing")]
    [InlineData("restriction", "own")]
    [InlineData("restriction", "denied")]
    [InlineData("unlink", "foreign")]
    [InlineData("unlink", "missing")]
    [InlineData("unlink", "own")]
    [InlineData("unlink", "denied")]
    [InlineData("sync", "foreign")]
    [InlineData("sync", "missing")]
    [InlineData("sync", "own")]
    [InlineData("sync", "denied")]
    public async Task ResourceMutation_RequiresResourceFromAuthorizedTeam(string action, string scenario)
    {
        var resourceId = Guid.NewGuid();
        _resources.CanManageTeamResourcesAsync(_team.Id, _userId, Arg.Any<CancellationToken>())
            .Returns(!string.Equals(scenario, "denied", StringComparison.Ordinal));
        if (!string.Equals(scenario, "missing", StringComparison.Ordinal))
            _resources.GetResourceByIdAsync(resourceId, Arg.Any<CancellationToken>()).Returns(
                new GoogleResourceSnapshot(resourceId,
                    string.Equals(scenario, "foreign", StringComparison.Ordinal) ? Guid.NewGuid() : _team.Id,
                    "google-id", "Folder", GoogleResourceType.DriveFolder, null));
        _resources.SetRestrictInheritedAccessWithResultAsync(resourceId, false, _userId, CancellationToken.None)
            .Returns(TeamResourceMutationResult.Success());
        _sync.SyncSingleResourceAsync(resourceId, SyncAction.Execute, CancellationToken.None)
            .Returns(new ResourceSyncDiff { ResourceId = resourceId });
        var controller = BuildSut();

        var result = action switch
        {
            "permission" => await controller.UpdatePermissionLevel(_team.Slug, resourceId, DrivePermissionLevel.Viewer),
            "restriction" => await controller.ToggleRestrictInheritedAccess(_team.Slug, resourceId, false),
            "unlink" => await controller.UnlinkResource(_team.Slug, resourceId),
            _ => await controller.SyncResource(_team.Slug, resourceId)
        };

        if (string.Equals(scenario, "denied", StringComparison.Ordinal)) Assert.IsType<ForbidResult>(result);
        else if (!string.Equals(scenario, "own", StringComparison.Ordinal)) Assert.IsType<NotFoundResult>(result);
        else Assert.IsType<RedirectToActionResult>(result);
        var mutationCalls = _resources.ReceivedCalls().Where(c => !IsRead(c.GetMethodInfo().Name)).ToList();
        if (string.Equals(scenario, "own", StringComparison.Ordinal))
        {
            if (string.Equals(action, "sync", StringComparison.Ordinal))
            {
                Assert.Single(_sync.ReceivedCalls());
                Assert.Empty(mutationCalls);
            }
            else
            {
                Assert.Single(mutationCalls);
                Assert.Empty(_sync.ReceivedCalls());
            }
        }
        else
        {
            Assert.Empty(mutationCalls);
            Assert.Empty(_sync.ReceivedCalls());
        }
    }

    [HumansTheory]
    [InlineData("index")]
    [InlineData("linkdrive")]
    [InlineData("linkgroup")]
    [InlineData("permission")]
    [InlineData("restriction")]
    [InlineData("unlink")]
    [InlineData("sync")]
    public async Task UnknownTeam_IsNotFound_AndNeverReachesResources(string action)
    {
        var controller = BuildSut();

        var result = await ActAsync(controller, action, "no-such-team");

        result.Should().BeOfType<NotFoundResult>();
        _resources.ReceivedCalls().Should().BeEmpty();
        _sync.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("index")]
    [InlineData("linkdrive")]
    [InlineData("linkgroup")]
    public async Task NonManager_IsForbidden_AndNeverMutates(string action)
    {
        _resources.CanManageTeamResourcesAsync(_team.Id, _userId, Arg.Any<CancellationToken>()).Returns(false);
        var controller = BuildSut();

        var result = await ActAsync(controller, action, _team.Slug);

        result.Should().BeOfType<ForbidResult>();
        _resources.ReceivedCalls().Where(c => !IsRead(c.GetMethodInfo().Name)).Should().BeEmpty();
        await _resources.DidNotReceive().GetTeamResourcesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public void EveryPostAction_CarriesItsOwnAuthorizeAttribute()
    {
        var posts = typeof(TeamResourcesController).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).Length > 0)
            .ToList();

        posts.Should().HaveCount(6);
        posts.Should().OnlyContain(m => m.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Length > 0);
    }

    [HumansFact]
    public async Task Index_AcceptsTeamGuidInTheSlugSlot()
    {
        _teams.GetTeamAsync(_team.Id, Arg.Any<CancellationToken>()).Returns(_team);
        var controller = BuildSut(RoleNames.Admin);

        var result = await controller.Index(_team.Id.ToString());

        result.Should().BeOfType<ViewResult>();
    }

    [HumansTheory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Board)]
    [InlineData(RoleNames.TeamsAdmin)]
    public async Task TeamsAdminBoardOrAdmin_ManagesAnyTeam_WithoutTheResourceManagerCheck(string role)
    {
        var controller = BuildSut(role);

        var result = await controller.Index(_team.Slug);

        result.Should().BeOfType<ViewResult>();
        await _resources.DidNotReceive().CanManageTeamResourcesAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubTeam_IsGatedAtItsDepartment(bool departmentManager)
    {
        var departmentId = Guid.NewGuid();
        var subTeam = Team(Guid.NewGuid(), parentTeamId: departmentId) with { Slug = "sub-team" };
        _teams.GetTeamBySlugAsync(subTeam.Slug, Arg.Any<CancellationToken>()).Returns(subTeam);
        _resources.CanManageTeamResourcesAsync(departmentId, _userId, Arg.Any<CancellationToken>()).Returns(departmentManager);
        _resources.CanManageTeamResourcesAsync(subTeam.Id, _userId, Arg.Any<CancellationToken>()).Returns(true);
        var controller = BuildSut();

        var result = await controller.Index(subTeam.Slug);

        if (departmentManager) result.Should().BeOfType<ViewResult>();
        else result.Should().BeOfType<ForbidResult>();
        await _resources.DidNotReceive().CanManageTeamResourcesAsync(subTeam.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    public async Task InheritedAccess_InvalidOrMissingChoice_DoesNotDispatch(bool allowed, bool invalidBinding, bool missingChoice)
    {
        var resourceId = Guid.NewGuid();
        _resources.GetResourceByIdAsync(resourceId, Arg.Any<CancellationToken>()).Returns(
            new GoogleResourceSnapshot(resourceId, _team.Id, "google-id", "Folder", GoogleResourceType.DriveFolder, null));
        _resources.CanManageTeamResourcesAsync(_team.Id, _userId, Arg.Any<CancellationToken>()).Returns(allowed);
        _resources.SetRestrictInheritedAccessWithResultAsync(resourceId, false, _userId, CancellationToken.None)
            .Returns(TeamResourceMutationResult.Success());
        var controller = BuildSut();
        var http = controller.HttpContext;
        if (invalidBinding) controller.ModelState.AddModelError("restrict", "Not a boolean.");
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        if (missingChoice)
        {
            http.RequestServices = services;
            var parameter = typeof(TeamResourcesController).GetMethod(nameof(TeamResourcesController.ToggleRestrictInheritedAccess))!.GetParameters()[2];
            var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
            var descriptor = new ControllerParameterDescriptor
            {
                Name = "restrict",
                ParameterType = parameter.ParameterType,
                ParameterInfo = parameter
            };
            var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
            {
                Metadata = metadata,
                CacheToken = parameter
            });
            await services.GetRequiredService<ParameterBinder>().BindModelAsync(controller.ControllerContext, binder,
                new FormValueProvider(BindingSource.Form, new FormCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal)), CultureInfo.InvariantCulture),
                descriptor, metadata, value: null);
        }

        var result = await controller.ToggleRestrictInheritedAccess(_team.Slug, resourceId, false);

        if (!allowed) Assert.IsType<ForbidResult>(result);
        else if (invalidBinding || missingChoice) Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<RedirectToActionResult>(result);
        if (!allowed || invalidBinding || missingChoice)
            await _resources.DidNotReceive().SetRestrictInheritedAccessWithResultAsync(
                Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        else await _resources.Received(1).SetRestrictInheritedAccessWithResultAsync(resourceId, false, _userId, CancellationToken.None);
    }

    [HumansTheory]
    [InlineData("viewer")]
    [InlineData("team")]
    [InlineData("permission")]
    [InlineData("resources")]
    [InlineData("account")]
    public async Task Index_ObservesRequestCancellationAtEveryRead(string boundary)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        _resources.CanManageTeamResourcesAsync(_team.Id, _userId, Arg.Any<CancellationToken>()).Returns(true);
        switch (boundary)
        {
            case "viewer":
                _users.GetUserInfoAsync(_userId, aborted.Token).Returns(ValueTask.FromCanceled<UserInfo?>(aborted.Token));
                break;
            case "team":
                _teams.GetTeamBySlugAsync(_team.Slug, aborted.Token).Returns(Task.FromCanceled<TeamInfo?>(aborted.Token));
                break;
            case "permission":
                _resources.CanManageTeamResourcesAsync(_team.Id, _userId, aborted.Token).Returns(Task.FromCanceled<bool>(aborted.Token));
                break;
            case "resources":
                _resources.GetTeamResourcesAsync(_team.Id, aborted.Token)
                    .Returns(Task.FromCanceled<IReadOnlyList<GoogleResourceSnapshot>>(aborted.Token));
                break;
            case "account":
                _resources.GetServiceAccountEmailAsync(aborted.Token).Returns(Task.FromCanceled<string>(aborted.Token));
                break;
        }
        var controller = BuildSut();
        controller.HttpContext.RequestAborted = aborted.Token;

        var act = () => controller.Index(_team.Slug);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static bool IsRead(string methodName) =>
        methodName is nameof(ITeamResourceService.CanManageTeamResourcesAsync)
            or nameof(ITeamResourceService.GetResourceByIdAsync)
            or nameof(ITeamResourceService.GetTeamResourcesAsync)
            or nameof(ITeamResourceService.GetServiceAccountEmailAsync);

    private static Task<IActionResult> ActAsync(TeamResourcesController controller, string action, string slug)
    {
        var resourceId = Guid.NewGuid();
        return action switch
        {
            "index" => controller.Index(slug),
            "linkdrive" => controller.LinkDriveResource(slug, new() { ResourceUrl = "https://drive.google.com/drive/folders/x" }),
            "linkgroup" => controller.LinkGroup(slug, new() { GroupEmail = "group@nobodies.team" }),
            "permission" => controller.UpdatePermissionLevel(slug, resourceId, DrivePermissionLevel.Viewer),
            "restriction" => controller.ToggleRestrictInheritedAccess(slug, resourceId, true),
            "unlink" => controller.UnlinkResource(slug, resourceId),
            _ => controller.SyncResource(slug, resourceId)
        };
    }

    private TeamResourcesController BuildSut(string? role = null)
    {
        var localizer = Substitute.For<IStringLocalizer<GoogleIntegrationResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, _userId.ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var authorization = new ServiceCollection()
            .AddLogging()
            .AddAuthorizationCore()
            .AddSingleton<IAuthorizationHandler>(new TeamResourceAuthorizationHandler(
                _resources, new HttpContextAccessor { HttpContext = http }))
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();
        return new TeamResourcesController(_users, _teams, _resources, authorization, _sync,
            NullLogger<TeamResourcesController>.Instance, localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
    }

    private static TeamInfo Team(Guid id, Guid? parentTeamId) =>
        new(id, "Team", null, "team", true, false, SystemTeamType.None,
            false, false, false, false, Instant.MinValue, [], ParentTeamId: parentTeamId);
}
