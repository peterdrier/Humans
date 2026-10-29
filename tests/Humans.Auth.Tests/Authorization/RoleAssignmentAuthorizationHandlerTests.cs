using System.Security.Claims;
using AwesomeAssertions;
using Humans.Auth.Authorization;
using Humans.Auth.Contracts;
using Humans.Auth.Tests.Infrastructure;
using Humans.Base.Constants;
using Humans.Camps.Contracts;
using Humans.Finance.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Auth.Tests.Authorization;

public sealed class RoleAssignmentAuthorizationHandlerTests
{
    private readonly RoleAssignmentAuthorizationHandler _handler = new();

    [HumansTheory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Board)]
    [InlineData(UsersRoles.UsersAdmin)]
    [InlineData(TeamsRoles.TeamsAdmin)]
    [InlineData(CampsRoles.CampsAdmin)]
    [InlineData(TicketsRoles.TicketsAdmin)]
    [InlineData(FinanceRoles.FinanceAdmin)]
    public async Task Admin_CanManageAnyRole(string roleName)
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        (await EvaluateAsync(user, roleName)).Should().BeTrue();
    }

    [HumansTheory]
    [InlineData(RoleNames.Board)]
    [InlineData(UsersRoles.UsersAdmin)]
    [InlineData(TeamsRoles.TeamsAdmin)]
    [InlineData(CampsRoles.CampsAdmin)]
    [InlineData(TicketsRoles.TicketsAdmin)]
    [InlineData(FinanceRoles.FinanceAdmin)]
    public async Task UsersAdmin_CanManageEveryRoleButAdmin(string roleName)
    {
        var user = CreateUserWithRoles(UsersRoles.UsersAdmin);
        (await EvaluateAsync(user, roleName)).Should().BeTrue();
    }

    [HumansFact]
    public async Task UsersAdmin_CannotManageAdminRole()
    {
        var user = CreateUserWithRoles(UsersRoles.UsersAdmin);
        (await EvaluateAsync(user, RoleNames.Admin)).Should().BeFalse();
    }

    [HumansTheory]
    [InlineData(RoleNames.Board)]
    [InlineData(TeamsRoles.TeamsAdmin)]
    [InlineData(RoleNames.Admin)]
    public async Task Board_NoLongerManagesRoles(string roleName)
    {
        // Board is a governance body, not an operator (Peter, 2026-10-22): role assignment moved to UsersAdmin.
        var user = CreateUserWithRoles(RoleNames.Board);
        (await EvaluateAsync(user, roleName)).Should().BeFalse();
    }

    [HumansTheory]
    [InlineData(TeamsRoles.TeamsAdmin)]
    [InlineData(FinanceRoles.FinanceAdmin)]
    public async Task OtherSectionAdmins_CannotManageRoles(string holderRole)
    {
        var user = CreateUserWithRoles(holderRole);
        (await EvaluateAsync(user, RoleNames.Board)).Should().BeFalse();
    }

    [HumansFact]
    public async Task RegularUser_DeniedForAnyRole()
    {
        var user = CreateUserWithRoles();
        (await EvaluateAsync(user, RoleNames.Board)).Should().BeFalse();
    }

    [HumansFact]
    public async Task UnauthenticatedUser_Denied()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        (await EvaluateAsync(user, RoleNames.Board)).Should().BeFalse();
    }

    [HumansFact]
    public void Assignable_FollowsTheSameRule_InCatalogOrder()
    {
        var catalog = TestRoles.Catalog();

        RoleAssignmentRules.Assignable(CreateUserWithRoles(RoleNames.Admin), catalog)
            .Select(r => r.Name).Should().Equal(catalog.Roles.Select(r => r.Name));
        RoleAssignmentRules.Assignable(CreateUserWithRoles(UsersRoles.UsersAdmin), catalog)
            .Select(r => r.Name).Should().Equal(catalog.Roles.Select(r => r.Name).Where(n => !string.Equals(n, RoleNames.Admin, StringComparison.Ordinal)));
        RoleAssignmentRules.Assignable(CreateUserWithRoles(RoleNames.Board), catalog).Should().BeEmpty();
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, string roleName)
    {
        var requirement = RoleAssignmentOperationRequirement.Manage;
        var context = new AuthorizationHandlerContext(
            [requirement], user, roleName);

        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsPrincipal CreateUserWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "test@example.com")
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
