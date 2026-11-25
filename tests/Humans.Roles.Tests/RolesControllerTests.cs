using System.Security.Claims;
using System.Globalization;
using Humans.Roles.Models;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Roles.Contracts;
using Humans.Roles.Controllers;
using Humans.Roles.Services;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Roles.Tests;

public sealed class RolesControllerTests
{
    [HumansTheory]
    [InlineData(false, "Production › Cantina")]
    [InlineData(true, "Cantina")]
    public void OwnerLabel_LeavesOutAHiddenParent(bool parentHidden, string expected)
    {
        var parent = new OwnerView(new Owner("Team", Guid.NewGuid()), "production", "Production", "/", parentHidden, true, null);
        var child = new OwnerView(new Owner("Team", Guid.NewGuid()), "cantina", "Cantina", "/", false, true, parent.Owner.Id);

        RolesController.OwnerLabel(child, [parent, child]).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void ToInput_LargeSlotCount_PreservesPostedPriorities(int slots)
    {
        var form = new RoleForm { Name = "Planner", SlotCount = slots, Priorities = [SlotPriority.Important] };

        var input = form.ToInput();

        input.SlotCount.Should().Be(slots);
        input.Priorities.Should().Equal(SlotPriority.Important);
    }

    public static IEnumerable<object?[]> GetFormCases()
    {
        foreach (var editing in new[] { false, true })
        {
            foreach (var field in new[] { "SlotCount", "SortOrder", "Period", "IsPublic", "IsCoordinator" })
            {
                yield return [editing, field, null, false, true];
                yield return [editing, field, "bad", false, true];
            }
            foreach (var field in new[] { "EstimatedHours", "Priorities" })
            {
                yield return [editing, field, "bad", false, true];
                yield return [editing, field, null, true, true];
            }
            yield return [editing, "EstimatedHours", "", true, true];
            yield return [editing, "Period", "bad", false, false];
        }
    }

    [HumansTheory]
    [MemberData(nameof(GetFormCases))]
    public async Task RoleForms_InvalidValues_RejectWrites(bool editing, string field, string? posted, bool valid, bool manages)
    {
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(
            UserFixtures.MakeUserInfo(actorId));
        var owner = new OwnerView(new Owner("Team", Guid.NewGuid()), "team", "Team", "/", false, true, null);
        var role = new RoleInfo(Guid.NewGuid(), owner.Owner, "Role", "role", null, null, 3, [], 0,
            RolePeriod.Event, null, false, false, [], []);
        var roles = Substitute.For<IRoleManagementService>();
        roles.GetOwnersAsync(Arg.Any<CancellationToken>()).Returns([owner]);
        roles.GetRolesAsync(Arg.Any<CancellationToken>()).Returns([role]);
        roles.CanManageAsync(owner.Owner, actorId, Arg.Any<CancellationToken>()).Returns(manages);
        roles.CreateAsync(owner.Owner, Arg.Any<RoleInput>(), actorId, Arg.Any<CancellationToken>()).Returns(new RoleResult(role));
        roles.UpdateAsync(role.Id, Arg.Any<RoleInput>(), actorId, Arg.Any<CancellationToken>()).Returns(new RoleResult(role));
        var localizer = Substitute.For<IStringLocalizer<RolesResource>>();
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(call => new LocalizedString(call.ArgAt<string>(0), call.ArgAt<string>(0)));
        var controller = new RolesController(roles, users, localizer, NullLogger<RolesController>.Instance);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "Test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        controller.Url.Action(Arg.Any<UrlActionContext>()).Returns("/Roles/Team/team/role");
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Name"] = "Role",
            ["SlotCount"] = "3",
            ["SortOrder"] = "0",
            ["Period"] = "Event",
            ["IsPublic"] = "false",
            ["IsCoordinator"] = "false",
            ["EstimatedHours"] = "6",
            ["Priorities"] = "None",
        };
        if (posted is null) fields.Remove(field);
        else fields[field] = posted;
        var parameter = typeof(RolesController).GetMethod(editing ? nameof(RolesController.Edit) : nameof(RolesController.Add))!
            .GetParameters().Single(p => string.Equals(p.Name, "form", StringComparison.Ordinal));
        var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext { Metadata = metadata });
        var descriptor = new ControllerParameterDescriptor
        {
            Name = parameter.Name!,
            ParameterType = parameter.ParameterType,
            ParameterInfo = parameter,
        };
        var binding = await services.GetRequiredService<ParameterBinder>().BindModelAsync(controller.ControllerContext, binder,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture), descriptor, metadata, value: null);
        var form = (RoleForm)binding.Model!;

        var result = editing
            ? await controller.Edit("Team", "team", "role", form, TestContext.Current.CancellationToken)
            : await controller.Add("Team", "team", form, TestContext.Current.CancellationToken);

        if (valid)
        {
            result.Should().BeOfType<RedirectResult>(string.Join("; ", controller.ModelState
                .SelectMany(entry => entry.Value!.Errors.Select(error => $"{entry.Key}: {error.ErrorMessage}"))));
            var expectedHours = string.Equals(field, "EstimatedHours", StringComparison.Ordinal) ? (int?)null : 6;
            var expectedPriorities = string.Equals(field, "Priorities", StringComparison.Ordinal) ? 0 : 1;
            if (editing)
                await roles.Received(1).UpdateAsync(role.Id, Arg.Is<RoleInput>(input => input.SlotCount == 3
                    && input.Period == RolePeriod.Event && !input.IsPublic && !input.IsCoordinator && input.SortOrder == 0
                    && input.EstimatedHours == expectedHours && input.Priorities.Count == expectedPriorities), actorId, Arg.Any<CancellationToken>());
            else
                await roles.Received(1).CreateAsync(owner.Owner, Arg.Is<RoleInput>(input => input.SlotCount == 3
                    && input.Period == RolePeriod.Event && !input.IsPublic && !input.IsCoordinator && input.SortOrder == 0
                    && input.EstimatedHours == expectedHours && input.Priorities.Count == expectedPriorities), actorId, Arg.Any<CancellationToken>());
        }
        else
        {
            if (manages) result.Should().BeOfType<BadRequestObjectResult>();
            else result.Should().BeOfType<ForbidResult>();
            await roles.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default);
            await roles.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        }
    }

    [HumansFact]
    public void Admin_IsForRolesReaders() =>
        typeof(RolesController).GetMethod(nameof(RolesController.Admin))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Cast<AuthorizeAttribute>()
            .Should().ContainSingle(a => a.Policy == RolesPolicies.Read);
}
