using Humans.Tickets.Contracts;
using Humans.Finance.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Grants.Authorization;
using Humans.Grants.Services;
using Humans.Grants.Controllers;
using Humans.Grants.Models;
using Humans.Grants.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Grants.Tests.Authorization;

/// <summary>Who may do what, including the deny paths the section doc lists.</summary>
public sealed class AuthorizationHandlerTests : GrantsTestHarness
{
    private static ClaimsPrincipal Principal(Guid userId, params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)).Prepend(new Claim(ClaimTypes.NameIdentifier, userId.ToString())),
            authenticationType: "test"));

    private async Task<bool> ProgramAllowedAsync(ClaimsPrincipal user, ProgramInfo program, ProgramOperationRequirement requirement)
    {
        var context = new AuthorizationHandlerContext([requirement], user, program);
        await new ProgramAuthorizationHandler(Users).HandleAsync(context);
        return context.HasSucceeded;
    }

    private async Task<bool> PolicyAllowedAsync(ClaimsPrincipal user)
    {
        var context = new AuthorizationHandlerContext([new ManagesGrantsRequirement()], user, resource: null);
        await new GrantsPolicyHandler(NewService(), Users).HandleAsync(context);
        return context.HasSucceeded;
    }

    [HumansFact]
    public async Task ManagerOfA_ManagesA_NotB_NeverAdministers()
    {
        var manager = SeedUser("Manager");
        var a = (await NewService().GetProgramAsync((await SeedProgramAsync(name: "A", managers: manager)).Id, Ct))!;
        var b = (await NewService().GetProgramAsync((await SeedProgramAsync(name: "B")).Id, Ct))!;
        var user = Principal(manager);

        (await ProgramAllowedAsync(user, a, ProgramOperationRequirement.Manage)).Should().BeTrue();
        (await ProgramAllowedAsync(user, b, ProgramOperationRequirement.Manage)).Should().BeFalse();
        (await ProgramAllowedAsync(user, a, ProgramOperationRequirement.Administer)).Should().BeFalse();
        (await PolicyAllowedAsync(user)).Should().BeTrue();
    }

    [HumansTheory]
    [Xunit.InlineData(RoleNames.Admin)]
    [Xunit.InlineData(RoleNames.Board)]
    public async Task AdminAndBoard_ManageAndAdministerEveryProgram(string role)
    {
        var program = (await NewService().GetProgramAsync((await SeedProgramAsync()).Id, Ct))!;
        var user = Principal(SeedUser("Role holder"), role);

        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Manage)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Administer)).Should().BeTrue();
        (await PolicyAllowedAsync(user)).Should().BeTrue();
    }

    [HumansTheory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData(FinanceRoles.FinanceAdmin)]
    [Xunit.InlineData(TicketsRoles.TicketsAdmin)]
    public async Task OtherPeople_AreDenied_EvenGrantHolders(string? role)
    {
        var person = SeedUser("Holder");
        var program = await SeedProgramAsync();
        await SeedGrantAsync(program, person);
        var info = (await NewService().GetProgramAsync(program.Id, Ct))!;
        var user = role is null ? Principal(person) : Principal(person, role);

        (await PolicyAllowedAsync(user)).Should().BeFalse();
        (await ProgramAllowedAsync(user, info, ProgramOperationRequirement.Manage)).Should().BeFalse();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Index_ShowsProgramsManagedThroughAnyMergedId_AndHidesUnrelatedPrograms(bool archivedCookie)
    {
        var archived = Guid.NewGuid();
        var survivor = SeedUser("Survivor", mergedIds: [archived]);
        AliasMergedId(archived, survivor);
        var throughAlias = await SeedProgramAsync(name: "Archived manager", managers: archived);
        var throughSurvivor = await SeedProgramAsync(name: "Current manager", managers: survivor);
        await SeedProgramAsync(name: "Other manager", managers: SeedUser("Other"));
        var viewer = Principal(archivedCookie ? archived : survivor);
        var service = NewService();
        var program = (await service.GetProgramAsync(throughAlias.Id, Ct))!;
        (await PolicyAllowedAsync(viewer)).Should().BeTrue();
        (await ProgramAllowedAsync(viewer, program, ProgramOperationRequirement.Manage)).Should().BeTrue();
        var controller = new GrantsAdminController(service, Users, Substitute.For<IAuthorizationService>(), Settings,
            NullLogger<GrantsAdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = viewer } }
        };

        var result = await controller.Index(Ct);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<AdminIndexViewModel>().Subject;
        model.CanAdminister.Should().BeFalse();
        model.Programs.Select(p => p.Id).Should().BeEquivalentTo([throughAlias.Id, throughSurvivor.Id]);
    }

    [HumansFact]
    public async Task ACookieStillCarryingAMergedAwayId_ManagesTheSurvivorsProgram()
    {
        var archived = Guid.NewGuid();
        var survivor = SeedUser("Survivor", mergedIds: [archived]);
        AliasMergedId(archived, survivor);
        var program = (await NewService().GetProgramAsync((await SeedProgramAsync(managers: survivor)).Id, Ct))!;
        var user = Principal(archived);

        (await PolicyAllowedAsync(user)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Manage)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Administer)).Should().BeFalse();
    }
}
