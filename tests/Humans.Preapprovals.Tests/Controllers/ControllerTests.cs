using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Preapprovals.Authorization;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Controllers;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Models;
using Humans.Preapprovals.Services;
using Humans.Preapprovals.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Humans.Preapprovals.Tests.Controllers;

/// <summary>
/// The controllers' deny paths and the spec's HTTP rules: a member never reaches the admin
/// controller, a manager of A never acts on B, and an Apply POST to a program not accepting
/// applications is 400, not a redirect.
/// </summary>
public sealed class ControllerTests : PreapprovalsTestHarness
{
    private readonly IStringLocalizer<PreapprovalsResource> _localizer = Substitute.For<IStringLocalizer<PreapprovalsResource>>();

    [HumansFact]
    public void AdminController_IsGatedByTheManagePolicy_MemberByAccess()
    {
        typeof(PreapprovalsAdminController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Single().Policy.Should().Be(PreapprovalsPolicies.Manage);
        typeof(PreapprovalsController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Single().Policy.Should().Be(PreapprovalsPolicies.Access);
        typeof(PreapprovalsAdminController).GetCustomAttributes(typeof(RouteAttribute), true)
            .Cast<RouteAttribute>().Single().Template.Should().Be("Preapprovals/Admin");
    }

    [HumansFact]
    public async Task ManagerOfA_OnB_IsForbidden_AndSeesOnlyA()
    {
        var manager = SeedUser("Manager");
        var a = await SeedProgramAsync(name: "A", managers: manager);
        var b = await SeedProgramAsync(name: "B");
        var applicationOnB = await SeedGrantAsync(b, status: GrantStatus.Applied);
        var controller = BuildAdmin(manager);

        (await controller.Program(b.Id, Ct)).Should().BeOfType<ForbidResult>();
        (await controller.Issue(b.Id, SeedUser(), null, null, Ct)).Should().BeOfType<ForbidResult>();
        (await controller.Approve(b.Id, applicationOnB.Id, null, Ct)).Should().BeOfType<ForbidResult>();
        (await controller.Edit(a.Id, Ct)).Should().BeOfType<ForbidResult>("managers never edit the program itself");
        (await controller.New(Ct)).Should().BeOfType<ForbidResult>();
        (await ReadGrantAsync(applicationOnB.Id)).Status.Should().Be(GrantStatus.Applied);

        var index = (await controller.Index(Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<AdminIndexViewModel>().Subject;
        index.Programs.Select(p => p.Id).Should().Equal(a.Id);
        index.CanAdminister.Should().BeFalse();
    }

    [HumansFact]
    public async Task GrantOfAnotherProgram_UnderMyProgramsUrl_IsNotFound()
    {
        var manager = SeedUser("Manager");
        var a = await SeedProgramAsync(name: "A", managers: manager);
        var b = await SeedProgramAsync(name: "B");
        var applicationOnB = await SeedGrantAsync(b, status: GrantStatus.Applied);
        var openOnB = await SeedGrantAsync(b);
        var controller = BuildAdmin(manager);

        (await controller.Approve(a.Id, applicationOnB.Id, null, Ct)).Should().BeOfType<NotFoundResult>();
        (await controller.Decline(a.Id, applicationOnB.Id, null, Ct)).Should().BeOfType<NotFoundResult>();
        (await controller.Revoke(a.Id, openOnB.Id, null, Ct)).Should().BeOfType<NotFoundResult>();
        (await controller.Amount(a.Id, openOnB.Id, 1m, Ct)).Should().BeOfType<NotFoundResult>();
        (await ReadGrantAsync(applicationOnB.Id)).Status.Should().Be(GrantStatus.Applied);
        (await ReadGrantAsync(openOnB.Id)).Status.Should().Be(GrantStatus.Open);
    }

    [HumansFact]
    public async Task Manager_ApprovesOnOwnProgram_Redirects()
    {
        var manager = SeedUser("Manager");
        var a = await SeedProgramAsync(managers: manager);
        var application = await SeedGrantAsync(a, SeedUser("Applicant"), status: GrantStatus.Applied);

        var result = await BuildAdmin(manager).Approve(a.Id, application.Id, null, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(PreapprovalsAdminController.Program));
        (await ReadGrantAsync(application.Id)).Status.Should().Be(GrantStatus.Open);
    }

    [HumansTheory]
    [Xunit.InlineData(RoleNames.Board)]
    [Xunit.InlineData(RoleNames.FinanceAdmin)]
    [Xunit.InlineData(RoleNames.Admin)]
    public async Task ManagingRole_CreatesAProgram(string role)
    {
        var controller = BuildAdmin(SeedUser("Role holder"), role);
        var model = new ProgramFormViewModel
        {
            Name = "2026 Creativity",
            Kind = ProgramKind.FixedPerPerson,
            PerPersonAmount = 300m,
            HoldedAccountNumber = 62900150,
            ClaimBy = Today.PlusMonths(6),
            AcceptsApplications = true,
            Status = ProgramStatus.Open
        };

        var result = await controller.New(model, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        (await NewService().GetProgramsAsync(Ct)).Should().ContainSingle().Which.Name.Should().Be("2026 Creativity");
    }

    [HumansFact]
    public async Task Apply_WhenNotAccepting_Is400_AndFormHidden()
    {
        var program = await SeedProgramAsync(acceptsApplications: false);
        var person = SeedUser("Person");
        await SeedGrantAsync(program, person);
        var controller = BuildMember(person);

        var post = await controller.Apply(program.Id, new ApplyFormViewModel { Note = "please" }, Ct);
        post.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().Be(PreapprovalErrorKeys.NotAcceptingApplications);
        (await ReadGrantsCountAsync()).Should().Be(1);

        var get = (await controller.Program(program.Id, Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<MemberProgramViewModel>().Subject;
        get.Program.IsAcceptingApplications(Today).Should().BeFalse();
    }

    [HumansFact]
    public async Task Apply_Accepted_RedirectsWithSuccess_AndAmountRuleShowsAsError()
    {
        var program = await SeedProgramAsync(kind: ProgramKind.PerGrantAmount, perPerson: null);
        var person = SeedUser("Person");
        var controller = BuildMember(person);

        (await controller.Apply(program.Id, new ApplyFormViewModel { Note = "Fire Lotus" }, Ct))
            .Should().BeOfType<RedirectToActionResult>();
        controller.TempData["ErrorMessage"].Should().Be(PreapprovalErrorKeys.AmountRequired);
        controller.TempData.Clear();

        (await controller.Apply(program.Id, new ApplyFormViewModel { Note = "Fire Lotus", RequestedAmount = 250m }, Ct))
            .Should().BeOfType<RedirectToActionResult>();
        controller.TempData["SuccessMessage"].Should().Be("Preapprovals_Applied");
        (await ReadSingleGrantAsync()).RequestedAmount.Should().Be(250m);
    }

    [HumansFact]
    public async Task Member_Index_ListsOwnGrantsAndOpenPrograms_NoManageFlag()
    {
        var mine = await SeedProgramAsync(name: "Mine", acceptsApplications: false);
        var open = await SeedProgramAsync(name: "Open");
        await SeedProgramAsync(name: "Closed", status: ProgramStatus.Closed);
        var person = SeedUser("Person");
        var grant = await SeedGrantAsync(mine, person);
        await SeedGrantAsync(mine);

        var model = (await BuildMember(person).Index(Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<MemberIndexViewModel>().Subject;

        model.Grants.Select(g => g.Id).Should().Equal(grant.Id);
        model.OpenPrograms.Select(p => p.Id).Should().Equal(open.Id);
        model.CanManage.Should().BeFalse();
    }

    private async Task<int> ReadGrantsCountAsync()
    {
        await using var db = OpenContext();
        return await db.Grants.CountAsync(Ct);
    }

    private async Task<PreapprovalGrant> ReadSingleGrantAsync()
    {
        await using var db = OpenContext();
        return await db.Grants.SingleAsync(Ct);
    }

    // ── Builders ──────────────────────────────────────────────────────────

    private PreapprovalsAdminController BuildAdmin(Guid actorId, params string[] roles)
    {
        var (provider, http) = Host(actorId, roles);
        var controller = new PreapprovalsAdminController(NewService(), Users, Finance, _localizer,
            provider.GetRequiredService<IAuthorizationService>(), Clock, Substitute.For<ILogger<PreapprovalsAdminController>>());
        Wire(controller, http);
        return controller;
    }

    private PreapprovalsController BuildMember(Guid actorId, params string[] roles)
    {
        var (provider, http) = Host(actorId, roles);
        var controller = new PreapprovalsController(NewService(), Users, _localizer,
            provider.GetRequiredService<IAuthorizationService>(), Clock, Substitute.For<ILogger<PreapprovalsController>>());
        Wire(controller, http);
        return controller;
    }

    private (ServiceProvider Provider, DefaultHttpContext Http) Host(Guid actorId, string[] roles)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(new SectionPolicies().AddPolicies);
        services.AddScoped<IAuthorizationHandler, ProgramAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler>(_ => new PreapprovalsPolicyHandler(NewService()));
        var provider = services.BuildServiceProvider();
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(call => new LocalizedString(call.ArgAt<string>(0), call.ArgAt<string>(0)));
        _localizer[Arg.Any<string>()]
            .Returns(call => new LocalizedString(call.ArgAt<string>(0), call.ArgAt<string>(0)));

        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r))
            .Prepend(new Claim(ClaimTypes.NameIdentifier, actorId.ToString()));
        var http = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"))
        };
        return (provider, http);
    }

    private static void Wire(Controller controller, DefaultHttpContext http)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Test" }
        };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
    }
}
