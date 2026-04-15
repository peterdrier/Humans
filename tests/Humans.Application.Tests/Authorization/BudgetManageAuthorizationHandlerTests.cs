using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Authorization;
using Humans.Domain.Constants;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Unit tests for BudgetManageAuthorizationHandler — evaluates
/// <see cref="BudgetOperationRequirement.Manage"/> for budget-wide admin mutations
/// (budget year lifecycle, groups, categories, projection parameters, sync jobs).
/// </summary>
public sealed class BudgetManageAuthorizationHandlerTests
{
    private readonly BudgetManageAuthorizationHandler _handler = new();

    [Fact]
    public async Task Admin_CanManage()
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var result = await EvaluateAsync(user);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task FinanceAdmin_CanManage()
    {
        var user = CreateUserWithRoles(RoleNames.FinanceAdmin);
        var result = await EvaluateAsync(user);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task SystemPrincipal_CanManage()
    {
        var result = await EvaluateAsync(SystemPrincipal.Instance);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Board_CannotManage()
    {
        var user = CreateUserWithRoles(RoleNames.Board);
        var result = await EvaluateAsync(user);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RegularUser_CannotManage()
    {
        var user = CreateUserWithRoles();
        var result = await EvaluateAsync(user);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task UnauthenticatedUser_CannotManage()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await EvaluateAsync(user);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task EditRequirement_NotHandled_ByManageHandler()
    {
        // Even an Admin should not have this handler succeed the Edit requirement —
        // that requirement is scoped to BudgetCategory resources via a different handler.
        var user = CreateUserWithRoles(RoleNames.Admin);
        var requirement = BudgetOperationRequirement.Edit;
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user)
    {
        var requirement = BudgetOperationRequirement.Manage;
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsPrincipal CreateUserWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "user@example.com")
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
