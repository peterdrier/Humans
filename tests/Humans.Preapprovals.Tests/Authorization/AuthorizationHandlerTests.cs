using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Preapprovals.Authorization;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Services;
using Humans.Preapprovals.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Preapprovals.Tests.Authorization;

/// <summary>Who may do what, including the deny paths the section doc lists.</summary>
public sealed class AuthorizationHandlerTests : PreapprovalsTestHarness
{
    private static ClaimsPrincipal Principal(Guid userId, params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)).Prepend(new Claim(ClaimTypes.NameIdentifier, userId.ToString())),
            authenticationType: "test"));

    private static async Task<bool> ProgramAllowedAsync(ClaimsPrincipal user, ProgramInfo program, ProgramOperationRequirement requirement)
    {
        var context = new AuthorizationHandlerContext([requirement], user, program);
        await new ProgramAuthorizationHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    private async Task<bool> PolicyAllowedAsync(ClaimsPrincipal user, IAuthorizationRequirement requirement)
    {
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await new PreapprovalsPolicyHandler(NewService()).HandleAsync(context);
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
    }

    [HumansTheory]
    [Xunit.InlineData(RoleNames.Admin)]
    [Xunit.InlineData(RoleNames.Board)]
    [Xunit.InlineData(RoleNames.FinanceAdmin)]
    public async Task ManagingRoles_ManageAndAdministerEveryProgram(string role)
    {
        var program = (await NewService().GetProgramAsync((await SeedProgramAsync()).Id, Ct))!;
        var user = Principal(SeedUser("Role holder"), role);

        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Manage)).Should().BeTrue();
        (await ProgramAllowedAsync(user, program, ProgramOperationRequirement.Administer)).Should().BeTrue();
        (await PolicyAllowedAsync(user, new ManagesPreapprovalsRequirement())).Should().BeTrue();
        (await PolicyAllowedAsync(user, new PreapprovalsAccessRequirement())).Should().BeTrue();
    }

    [HumansFact]
    public async Task Member_WithNothing_IsDeniedBothPolicies()
    {
        await SeedProgramAsync(status: ProgramStatus.Closed);
        var user = Principal(SeedUser("Nobody"));
        (await PolicyAllowedAsync(user, new ManagesPreapprovalsRequirement())).Should().BeFalse();
        (await PolicyAllowedAsync(user, new PreapprovalsAccessRequirement())).Should().BeFalse();
    }

    [HumansFact]
    public async Task GrantHolder_HasAccess_NotManage()
    {
        var program = await SeedProgramAsync(acceptsApplications: false);
        var holder = SeedUser("Holder");
        await SeedGrantAsync(program, holder);
        var user = Principal(holder);
        (await PolicyAllowedAsync(user, new PreapprovalsAccessRequirement())).Should().BeTrue();
        (await PolicyAllowedAsync(user, new ManagesPreapprovalsRequirement())).Should().BeFalse();
    }

    [HumansFact]
    public async Task ProgramManager_HasBoth_AlsoUnderMergedId()
    {
        var oldId = Guid.NewGuid();
        var manager = SeedUser("Manager", mergedIds: [oldId]);
        await SeedProgramAsync(acceptsApplications: false, managers: oldId);
        var user = Principal(manager);
        (await PolicyAllowedAsync(user, new ManagesPreapprovalsRequirement())).Should().BeTrue();
        (await PolicyAllowedAsync(user, new PreapprovalsAccessRequirement())).Should().BeTrue();
    }

    [HumansFact]
    public async Task Anonymous_IsDenied()
    {
        await SeedProgramAsync();
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        (await PolicyAllowedAsync(user, new PreapprovalsAccessRequirement())).Should().BeFalse();
        (await PolicyAllowedAsync(user, new ManagesPreapprovalsRequirement())).Should().BeFalse();
    }
}
