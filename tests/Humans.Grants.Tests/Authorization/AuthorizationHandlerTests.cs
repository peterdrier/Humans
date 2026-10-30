using Humans.Tickets.Contracts;
using Humans.Finance.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Grants.Authorization;
using Humans.Grants.Contracts;
using Humans.Grants.Services;
using Humans.Grants.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;

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

        (await ProgramAllowedAsync(user, a, ProgramOperationRequirement.View)).Should().BeTrue();
        (await ProgramAllowedAsync(user, a, ProgramOperationRequirement.Manage)).Should().BeTrue();
        (await ProgramAllowedAsync(user, b, ProgramOperationRequirement.View)).Should().BeFalse();
        (await ProgramAllowedAsync(user, b, ProgramOperationRequirement.Manage)).Should().BeFalse();
        (await ProgramAllowedAsync(user, a, ProgramOperationRequirement.Administer)).Should().BeFalse();
        (await PolicyAllowedAsync(user)).Should().BeTrue();
    }

    [HumansTheory]
    [Xunit.InlineData(RoleNames.Admin)]
    [Xunit.InlineData(GrantsRoles.GrantsAdmin)]
    public async Task AdminAndGrantsAdmin_ManageAndAdministerEveryProgram(string role)
    {
        var program = (await NewService().GetProgramAsync((await SeedProgramAsync()).Id, Ct))!;
        var user = Principal(SeedUser("Role holder"), role);

        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.View)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Manage)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Administer)).Should().BeTrue();
        (await PolicyAllowedAsync(user)).Should().BeTrue();
    }

    [HumansFact]
    public async Task Board_ViewsEveryProgram_ButNeitherManagesNorAdministers()
    {
        var program = (await NewService().GetProgramAsync((await SeedProgramAsync()).Id, Ct))!;
        var user = Principal(SeedUser("Board member"), RoleNames.Board);

        (await PolicyAllowedAsync(user)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.View)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Manage)).Should().BeFalse();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Administer)).Should().BeFalse();
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
        (await ProgramAllowedAsync(user, info, ProgramOperationRequirement.View)).Should().BeFalse();
        (await ProgramAllowedAsync(user, info, ProgramOperationRequirement.Manage)).Should().BeFalse();
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
