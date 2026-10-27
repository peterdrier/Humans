using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Controllers;
using Humans.Teams.Models;
using Humans.Teams.Domain;
using Humans.Teams.Services;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Teams.Tests.Controllers;

public class TeamAdminControllerMembersTests
{
    [HumansTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public async Task RoleDefinitionWrite_UnexpectedDatabaseFailure_Propagates(int write, bool ajax)
    {
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
        var team = new TeamInfo(Guid.NewGuid(), "Team", null, "team", true, false, SystemTeamType.None,
            false, false, false, false, Instant.MinValue, []);
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo> { [team.Id] = team });
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), team, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        if (write == 3) http.User.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.Role, RoleNames.Admin)]));
        if (ajax) http.Request.Headers.XRequestedWith = "XMLHttpRequest";
        var controller = new TeamAdminController(teams, Substitute.For<ITeamResourceService>(), users,
            Substitute.For<IEmailProvisioningService>(), authorization, NullLogger<TeamAdminController>.Instance,
            Substitute.For<IStringLocalizer<TeamsResource>>(), Substitute.For<ITicketService>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        var failure = new Microsoft.EntityFrameworkCore.DbUpdateException("Private database diagnostic");
        teams.CreateRoleDefinitionAsync(default, default!, default, default, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<TeamRoleDefinitionMutationResult>(failure));
        teams.UpdateRoleDefinitionAsync(default, default!, default, default, default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<TeamRoleDefinitionMutationResult>(failure));
        teams.DeleteRoleDefinitionAsync(default, default).ReturnsForAnyArgs(Task.FromException<TeamRoleDefinitionMutationResult>(failure));

        teams.ToggleRoleIsManagementAsync(default, default)
            .ReturnsForAnyArgs(Task.FromException<TeamRoleDefinitionMutationResult>(failure));

        Func<Task<IActionResult>> act = () => write switch
        {
            0 => controller.CreateRole(team.Slug, new CreateRoleDefinitionModel { Name = "Role" }),
            1 => controller.EditRole(team.Slug, roleId,
                new EditRoleDefinitionModel { Name = "Role", SlotCount = 1, Priorities = ["None"] }),
            2 => controller.DeleteRole(team.Slug, roleId),
            _ => controller.ToggleManagement(team.Slug, roleId)
        };

        (await act.Should().ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>()).Which.Should().BeSameAs(failure);
        controller.TempData.Should().NotContainKey("ErrorMessage");
        controller.TempData.Should().NotContainKey("SuccessMessage");
    }

    [HumansTheory]
    [InlineData(true, 2000)]
    [InlineData(false, 2000)]
    [InlineData(true, 2001)]
    [InlineData(false, 2001)]
    public async Task JoinReviewForms_RejectOversizedNotesBeforeMutation(bool approve, int length)
    {
        var userId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
        var team = new TeamInfo(Guid.NewGuid(), "Team", null, "team", true, false, SystemTeamType.None,
            false, false, false, false, Instant.MinValue, []);
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo> { [team.Id] = team });
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), team, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        var localizer = Substitute.For<IStringLocalizer<TeamsResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = new TeamAdminController(teams, Substitute.For<ITeamResourceService>(),
            users, Substitute.For<IEmailProvisioningService>(), authorization, NullLogger<TeamAdminController>.Instance,
            localizer, Substitute.For<ITicketService>())
        {
            ControllerContext = new ControllerContext { HttpContext = http, ActionDescriptor = new ControllerActionDescriptor() },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        teams.ApproveJoinRequestAsync(requestId, userId, Arg.Any<string?>()).Returns(new TeamJoinReviewResult());
        teams.RejectJoinRequestAsync(requestId, userId, Arg.Any<string>()).Returns(new TeamJoinReviewResult());
        var form = new ApproveRejectRequestModel { Notes = new string('x', length) };
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", form);

        var result = approve
            ? await controller.ApproveRequest(team.Slug, requestId, form)
            : await controller.RejectRequest(team.Slug, requestId, form);

        if (length > 2000)
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            controller.ModelState[nameof(form.Notes)]!.Errors.Should().ContainSingle();
            teams.ReceivedCalls().Should().NotContain(call =>
                string.Equals(call.GetMethodInfo().Name, nameof(ITeamManagementService.ApproveJoinRequestAsync), StringComparison.Ordinal)
                || string.Equals(call.GetMethodInfo().Name, nameof(ITeamManagementService.RejectJoinRequestAsync), StringComparison.Ordinal));
            controller.TempData.Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            if (approve)
                await teams.Received(1).ApproveJoinRequestAsync(requestId, userId, form.Notes);
            else
                await teams.Received(1).RejectJoinRequestAsync(requestId, userId, form.Notes);
        }
    }

    [HumansTheory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "2026-02-31")]
    [InlineData(true, "2026-02-31")]
    public async Task EarlyEntryForms_InvalidDatesRedisplayWithoutMutation(bool edit, string date)
    {
        var userId = Guid.NewGuid();
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));
        var team = new TeamInfo(Guid.NewGuid(), "Team", null, "team", true, false, SystemTeamType.None,
            false, false, false, false, Instant.MinValue, [], EarlyEntryEnabled: true);
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo> { [team.Id] = team });
        teams.GetEarlyEntryGrantsForTeamAsync(team.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<TeamEarlyEntryGrantInfo>());
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), team, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        var controller = new TeamAdminController(teams, Substitute.For<ITeamResourceService>(),
            users, Substitute.For<IEmailProvisioningService>(), authorization, NullLogger<TeamAdminController>.Instance,
            Substitute.For<IStringLocalizer<TeamsResource>>(), Substitute.For<ITicketService>())
        {
            ControllerContext = new ControllerContext { HttpContext = http, ActionDescriptor = new ControllerActionDescriptor() },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };
        var formType = edit ? typeof(EditTeamEarlyEntryInput) : typeof(AddTeamEarlyEntryInput);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(formType);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = formType
        });
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["UserId"] = userId.ToString(),
            ["GrantId"] = Guid.NewGuid().ToString(),
            ["EntryDate"] = date,
            ["ProjectName"] = "Build"
        };
        var context = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(context);
        var model = context.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);

        var result = edit
            ? await controller.EditEarlyEntry(team.Slug, (EditTeamEarlyEntryInput)model, TestContext.Current.CancellationToken)
            : await controller.AddEarlyEntry(team.Slug, (AddTeamEarlyEntryInput)model, TestContext.Current.CancellationToken);

        var view = result.Should().BeOfType<ViewResult>().Which;
        view.ViewName.Should().Be(nameof(TeamAdminController.EarlyEntry));
        view.Model.Should().BeOfType<TeamEarlyEntryPageViewModel>().Which.TeamName.Should().Be(team.Name);
        controller.ModelState["EntryDate"]!.Errors.Should().ContainSingle();
        teams.ReceivedCalls().Should().NotContain(c =>
            string.Equals(c.GetMethodInfo().Name, nameof(ITeamManagementService.AddEarlyEntryGrantAsync), StringComparison.Ordinal)
            || string.Equals(c.GetMethodInfo().Name, nameof(ITeamManagementService.EditEarlyEntryGrantAsync), StringComparison.Ordinal));
        controller.TempData.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false, false, true, null)]
    [InlineData(true, false, true, null)]
    [InlineData(true, true, true, null)]
    [InlineData(false, false, false, null)]
    [InlineData(true, false, false, null)]
    [InlineData(true, true, false, null)]
    [InlineData(false, false, true, "SlotCount")]
    [InlineData(false, false, true, "SortOrder")]
    [InlineData(true, false, true, "SlotCount")]
    [InlineData(true, true, true, "SortOrder")]
    [InlineData(false, false, true, "")]
    [InlineData(true, true, true, "")]
    public async Task RoleForms_RejectInvalidOrMissingNumbersAndAcceptExplicitZeroOrder(bool edit, bool ajax, bool allowed, string? omitted)
    {
        var userId = Guid.NewGuid();
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
        var team = new TeamInfo(Guid.NewGuid(), "Team", null, "team", true, false, SystemTeamType.None,
            false, false, false, false, Instant.MinValue, []);
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo> { [team.Id] = team });
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), team, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        if (ajax) http.Request.Headers.XRequestedWith = "XMLHttpRequest";
        var controller = new TeamAdminController(teams, Substitute.For<ITeamResourceService>(),
            users,
            Substitute.For<IEmailProvisioningService>(), authorization, NullLogger<TeamAdminController>.Instance,
            Substitute.For<IStringLocalizer<TeamsResource>>(), Substitute.For<ITicketService>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        http.RequestServices = services;
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Name"] = "Role",
            ["SlotCount"] = "1",
            ["SortOrder"] = "0",
            ["Priorities"] = "None"
        };
        if (!string.IsNullOrEmpty(omitted)) fields.Remove(omitted);
        var modelType = edit ? typeof(EditRoleDefinitionModel) : typeof(CreateRoleDefinitionModel);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(modelType);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = modelType
        });
        var context = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(context);
        var form = context.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", form);
        if (omitted is null) controller.ModelState.AddModelError("EstimatedHours", "Invalid hours");

        teams.CreateRoleDefinitionAsync(default, default!, default, default, default!, default, default, default)
            .ReturnsForAnyArgs(new TeamRoleDefinitionMutationResult());
        teams.UpdateRoleDefinitionAsync(default, default!, default, default, default!, default, default, default, default)
            .ReturnsForAnyArgs(new TeamRoleDefinitionMutationResult());

        var result = edit
            ? await controller.EditRole(team.Slug, Guid.NewGuid(), (EditRoleDefinitionModel)form)
            : await controller.CreateRole(team.Slug, (CreateRoleDefinitionModel)form);

        if (allowed && string.Equals(omitted, "", StringComparison.Ordinal))
        {
            if (ajax) result.Should().BeOfType<JsonResult>();
            else result.Should().BeOfType<RedirectToActionResult>();
            var mutation = teams.ReceivedCalls().Should().ContainSingle(call =>
                string.Equals(call.GetMethodInfo().Name, edit ? nameof(ITeamManagementService.UpdateRoleDefinitionAsync)
                    : nameof(ITeamManagementService.CreateRoleDefinitionAsync), StringComparison.Ordinal)).Subject;
            mutation.GetArguments()[3].Should().Be(1);
            mutation.GetArguments()[5].Should().Be(0);
            return;
        }
        if (allowed)
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            if (omitted is not null) controller.ModelState[omitted]!.Errors.Should().ContainSingle();
        }
        else if (ajax)
            result.Should().BeOfType<UnauthorizedResult>();
        else
            result.Should().BeOfType<ForbidResult>();
        teams.ReceivedCalls().Should().NotContain(call =>
            string.Equals(call.GetMethodInfo().Name, nameof(ITeamManagementService.CreateRoleDefinitionAsync), StringComparison.Ordinal)
            || string.Equals(call.GetMethodInfo().Name, nameof(ITeamManagementService.UpdateRoleDefinitionAsync), StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task ProvisionEmail_ShowsCredentialRecoveryWarningForCommittedAccount()
    {
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = actorId }, [], [], [], null, []));
        var team = new TeamInfo(Guid.NewGuid(), "Team", null, "team", true, false, SystemTeamType.None,
            false, false, false, false, Instant.MinValue,
            [new TeamMemberInfo(Guid.NewGuid(), targetId, "Person", null, null, TeamMemberRole.Member, Instant.MinValue)]);
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo> { [team.Id] = team });
        teams.GetTeamAsync(team.Id, Arg.Any<CancellationToken>()).Returns(team);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), team, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var provisioning = Substitute.For<IEmailProvisioningService>();
        const string warning = "Account created; ask an admin to reset the password.";
        provisioning.ProvisionNobodiesEmailAsync(targetId, "person", actorId)
            .Returns(new EmailProvisioningResult(true, "person@nobodies.team", "personal@example.org", WarningMessage: warning));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"))
        };
        var controller = new TeamAdminController(teams, Substitute.For<ITeamResourceService>(),
            users, provisioning, authorization,
            NullLogger<TeamAdminController>.Instance, Substitute.For<IStringLocalizer<TeamsResource>>(),
            Substitute.For<ITicketService>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };

        var result = await controller.ProvisionEmail(team.Slug, targetId, "person");

        result.Should().BeOfType<RedirectToActionResult>();
        controller.TempData[TempDataKeys.InfoMessage].Should().Be(warning);
        controller.TempData[TempDataKeys.SuccessMessage].Should().BeNull();
        controller.TempData[TempDataKeys.ErrorMessage].Should().BeNull();
    }

    [HumansTheory]
    [InlineData("approve", false)]
    [InlineData("approve", true)]
    [InlineData("reject", false)]
    [InlineData("remove", false)]
    [InlineData("add", false)]
    [InlineData("reject", true)]
    [InlineData("remove", true)]
    [InlineData("add", true)]
    public async Task ExpectedRejection_PreservesFeedbackAndContextWithoutStack_UnexpectedFailurePropagates(
        string action, bool unexpected)
    {
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var instant = Instant.FromUtc(2026, 1, 1, 0, 0);
        var team = new TeamInfo(Guid.NewGuid(), "Team", null, "team", true, false, SystemTeamType.None,
            false, false, false, false, instant, []);
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = actorId }, [], [], [], null, [])));
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo> { [team.Id] = team });
        const string reason = "The requested membership change is not permitted";
        Exception failure = unexpected
            ? action is "approve" or "reject" or "add" ? new InvalidOperationException("Private dependency failure") : new IOException("Database unavailable")
            : new InvalidOperationException(reason);
        teams.ApproveJoinRequestAsync(targetId, actorId, "Reason").Returns(unexpected
            ? Task.FromException<TeamJoinReviewResult>(failure)
            : Task.FromResult(new TeamJoinReviewResult(Refusal: reason)));
        teams.RejectJoinRequestAsync(targetId, actorId, "Reason").Returns(unexpected
            ? Task.FromException<TeamJoinReviewResult>(failure)
            : Task.FromResult(new TeamJoinReviewResult(Refusal: reason)));
        teams.RemoveMemberAsync(team.Id, targetId, actorId).Returns(unexpected
            ? Task.FromException<TeamMemberRemovalResult>(new InvalidOperationException("Private dependency failure"))
            : Task.FromResult(new TeamMemberRemovalResult("Teams_RemoveMember_Permission")));
        teams.AddMemberToTeamAsync(team.Id, targetId, actorId).Returns(unexpected
            ? Task.FromException<TeamJoinReviewResult>(failure)
            : Task.FromResult(new TeamJoinReviewResult(Refusal: reason)));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), team, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var logger = Substitute.For<ILogger<TeamAdminController>>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "Test"))
        };
        var localizer = Substitute.For<IStringLocalizer<TeamsResource>>();
        localizer["Teams_RemoveMember_Permission"].Returns(new LocalizedString("Teams_RemoveMember_Permission", reason));
        var controller = new TeamAdminController(teams, Substitute.For<ITeamResourceService>(),
            users, Substitute.For<IEmailProvisioningService>(), authorization,
            logger, localizer, Substitute.For<ITicketService>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        Task<IActionResult> ActAsync() => action switch
        {
            "approve" => controller.ApproveRequest(team.Slug, targetId, new ApproveRejectRequestModel { Notes = "Reason" }),
            "reject" => controller.RejectRequest(team.Slug, targetId, new ApproveRejectRequestModel { Notes = "Reason" }),
            "remove" => controller.RemoveMember(team.Slug, targetId),
            _ => controller.AddMember(team.Slug, new AddMemberModel { UserId = targetId })
        };
        if (unexpected)
        {
            Func<Task> act = async () => await ActAsync();
            if (action is "remove" or "approve" or "reject" or "add") await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Private dependency failure");
            else await act.Should().ThrowAsync<IOException>();
            logger.ReceivedCalls().Should().BeEmpty();
            return;
        }

        var redirect = (await ActAsync()).Should().BeOfType<RedirectToActionResult>().Which;
        redirect.ActionName.Should().Be(nameof(TeamAdminController.Members));
        redirect.RouteValues!["slug"].Should().Be(team.Slug);
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be(reason);
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Warning);
        args[3].Should().BeNull();
        args[2]!.ToString().Should().Contain(string.Equals(action, "remove", StringComparison.Ordinal) ? "Teams_RemoveMember_Permission" : reason).And.Contain(actorId.ToString())
            .And.Contain(team.Id.ToString()).And.Contain(targetId.ToString());
    }

    [HumansTheory]
    [InlineData(int.MaxValue, 0, int.MaxValue)]
    [InlineData(int.MinValue, 1, 1)]
    [InlineData(0, 1, 1)]
    public async Task Members_HandlesOutOfRangePagesWithoutWrapping(int page, int expectedCount, int expectedPage)
    {
        var userId = Guid.NewGuid();
        var teams = Substitute.For<ITeamManagementService>();
        var users = Substitute.For<IUserService>();
        var instant = Instant.FromUtc(2026, 1, 1, 0, 0);
        var team = new TeamInfo(Guid.NewGuid(), "Team", null, "team", true, false, SystemTeamType.None,
            false, false, false, false, instant,
            [new TeamMemberInfo(Guid.NewGuid(), userId, "Human", null, null, TeamMemberRole.Member, instant)]);
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo> { [team.Id] = team });
        teams.GetPendingRequestsForTeamAsync(team.Id).Returns([]);
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = userId }, [], [], [], null, [])));
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), team, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var resources = Substitute.For<ITeamResourceService>();
        resources.GetTeamResourcesAsync(team.Id, Arg.Any<CancellationToken>()).Returns([]);
        var controller = new TeamAdminController(teams, resources, users,
            Substitute.For<IEmailProvisioningService>(), authorization, NullLogger<TeamAdminController>.Instance,
            Substitute.For<IStringLocalizer<TeamsResource>>(), Substitute.For<ITicketService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, RoleNames.Admin)], "Test")),
                }
            },
        };
        var first = (TeamMembersViewModel)((ViewResult)await controller.Members(team.Slug)).Model!;
        first.Members.Should().ContainSingle();

        var result = (TeamMembersViewModel)((ViewResult)await controller.Members(team.Slug, page)).Model!;
        result.Members.Should().HaveCount(expectedCount);
        result.PageNumber.Should().Be(expectedPage);
        result.TotalCount.Should().Be(1);
    }
}
