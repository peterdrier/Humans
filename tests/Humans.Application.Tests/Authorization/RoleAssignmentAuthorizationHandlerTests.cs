using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Authorization;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Unit tests for RoleAssignmentAuthorizationHandler — service-layer enforcement
/// for role assignment and removal operations.
/// Tests cover: Admin override, Board/HumanAdmin per-role checks, system principal,
/// and denial for unauthorized users.
/// </summary>
public sealed class RoleAssignmentAuthorizationHandlerTests
{
    private readonly RoleAssignmentAuthorizationHandler _handler = new();

    // --- Admin override ---

    [HumansTheory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Board)]
    [InlineData(RoleNames.TeamsAdmin)]
    [InlineData(RoleNames.CampAdmin)]
    [InlineData(RoleNames.TicketAdmin)]
    [InlineData(RoleNames.NoInfoAdmin)]
    [InlineData(RoleNames.FeedbackAdmin)]
    [InlineData(RoleNames.FinanceAdmin)]
    [InlineData(RoleNames.ConsentCoordinator)]
    [InlineData(RoleNames.VolunteerCoordinator)]
    [InlineData(RoleNames.HumanAdmin)]
    public async Task Admin_CanManageAnyRole(string roleName)
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var result = await EvaluateAsync(user, roleName);
        result.Should().BeTrue();
    }

    // --- Board access ---

    [HumansTheory]
    [InlineData(RoleNames.Board)]
    [InlineData(RoleNames.HumanAdmin)]
    [InlineData(RoleNames.TeamsAdmin)]
    [InlineData(RoleNames.CampAdmin)]
    [InlineData(RoleNames.TicketAdmin)]
    [InlineData(RoleNames.NoInfoAdmin)]
    [InlineData(RoleNames.ConsentCoordinator)]
    [InlineData(RoleNames.VolunteerCoordinator)]
    [InlineData(RoleNames.FeedbackAdmin)]
    [InlineData(RoleNames.FinanceAdmin)]
    public async Task Board_CanManageAllowedRoles(string roleName)
    {
        var user = CreateUserWithRoles(RoleNames.Board);
        var result = await EvaluateAsync(user, roleName);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Board_CannotManageAdminRole()
    {
        var user = CreateUserWithRoles(RoleNames.Board);
        var result = await EvaluateAsync(user, RoleNames.Admin);
        result.Should().BeFalse();
    }

    // --- HumanAdmin access ---

    [HumansTheory]
    [InlineData(RoleNames.Board)]
    [InlineData(RoleNames.HumanAdmin)]
    [InlineData(RoleNames.TeamsAdmin)]
    [InlineData(RoleNames.CampAdmin)]
    [InlineData(RoleNames.TicketAdmin)]
    [InlineData(RoleNames.NoInfoAdmin)]
    [InlineData(RoleNames.ConsentCoordinator)]
    [InlineData(RoleNames.VolunteerCoordinator)]
    [InlineData(RoleNames.FeedbackAdmin)]
    [InlineData(RoleNames.FinanceAdmin)]
    public async Task HumanAdmin_CanManageAllowedRoles(string roleName)
    {
        var user = CreateUserWithRoles(RoleNames.HumanAdmin);
        var result = await EvaluateAsync(user, roleName);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task HumanAdmin_CannotManageAdminRole()
    {
        var user = CreateUserWithRoles(RoleNames.HumanAdmin);
        var result = await EvaluateAsync(user, RoleNames.Admin);
        result.Should().BeFalse();
    }

    // --- Denial cases ---

    [HumansFact]
    public async Task RegularUser_DeniedForAnyRole()
    {
        var user = CreateUserWithRoles();
        var result = await EvaluateAsync(user, RoleNames.Board);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task UnauthenticatedUser_Denied()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await EvaluateAsync(user, RoleNames.Board);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task TeamsAdmin_CannotManageRoles()
    {
        var user = CreateUserWithRoles(RoleNames.TeamsAdmin);
        var result = await EvaluateAsync(user, RoleNames.Board);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task FinanceAdmin_CannotManageRoles()
    {
        var user = CreateUserWithRoles(RoleNames.FinanceAdmin);
        var result = await EvaluateAsync(user, RoleNames.Board);
        result.Should().BeFalse();
    }

    // --- Helpers ---

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
