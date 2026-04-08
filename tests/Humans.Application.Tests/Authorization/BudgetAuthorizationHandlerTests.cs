using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Unit tests for BudgetAuthorizationHandler — the first resource-based authorization handler.
/// Tests cover: Admin override, FinanceAdmin override, department coordinator access,
/// denial for non-coordinators, restricted groups, deleted years, null team, and edge cases.
/// </summary>
public sealed class BudgetAuthorizationHandlerTests
{
    private readonly IBudgetService _budgetService = Substitute.For<IBudgetService>();
    private readonly BudgetAuthorizationHandler _handler;

    private static readonly Guid CoordinatorTeamId = Guid.NewGuid();
    private static readonly Guid OtherTeamId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    public BudgetAuthorizationHandlerTests()
    {
        _handler = new BudgetAuthorizationHandler(_budgetService);

        _budgetService.GetEffectiveCoordinatorTeamIdsAsync(UserId)
            .Returns(new HashSet<Guid> { CoordinatorTeamId });
    }

    // --- Admin override ---

    [Fact]
    public async Task Admin_CanEditAnyCategory()
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var category = CreateCategory(OtherTeamId);

        var result = await EvaluateAsync(user, category);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_CanEditRestrictedGroupCategory()
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var category = CreateCategory(OtherTeamId, isRestricted: true);

        var result = await EvaluateAsync(user, category);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_CanEditDeletedYearCategory()
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var category = CreateCategory(OtherTeamId, isDeleted: true);

        var result = await EvaluateAsync(user, category);

        result.Should().BeTrue();
    }

    // --- FinanceAdmin override ---

    [Fact]
    public async Task FinanceAdmin_CanEditAnyCategory()
    {
        var user = CreateUserWithRoles(RoleNames.FinanceAdmin);
        var category = CreateCategory(OtherTeamId);

        var result = await EvaluateAsync(user, category);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task FinanceAdmin_CanEditRestrictedGroupCategory()
    {
        var user = CreateUserWithRoles(RoleNames.FinanceAdmin);
        var category = CreateCategory(OtherTeamId, isRestricted: true);

        var result = await EvaluateAsync(user, category);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task FinanceAdmin_CanEditCategoryWithNoTeam()
    {
        var user = CreateUserWithRoles(RoleNames.FinanceAdmin);
        var category = CreateCategory(teamId: null);

        var result = await EvaluateAsync(user, category);

        result.Should().BeTrue();
    }

    // --- Department coordinator access ---

    [Fact]
    public async Task Coordinator_CanEditOwnDepartmentCategory()
    {
        var user = CreateUser(UserId);
        var category = CreateCategory(CoordinatorTeamId);

        var result = await EvaluateAsync(user, category);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Coordinator_CannotEditOtherDepartmentCategory()
    {
        var user = CreateUser(UserId);
        var category = CreateCategory(OtherTeamId);

        var result = await EvaluateAsync(user, category);

        result.Should().BeFalse();
    }

    // --- Denial cases ---

    [Fact]
    public async Task Coordinator_DeniedOnRestrictedGroup()
    {
        var user = CreateUser(UserId);
        var category = CreateCategory(CoordinatorTeamId, isRestricted: true);

        var result = await EvaluateAsync(user, category);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Coordinator_DeniedOnDeletedYear()
    {
        var user = CreateUser(UserId);
        var category = CreateCategory(CoordinatorTeamId, isDeleted: true);

        var result = await EvaluateAsync(user, category);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Coordinator_DeniedOnCategoryWithNoTeam()
    {
        var user = CreateUser(UserId);
        var category = CreateCategory(teamId: null);

        var result = await EvaluateAsync(user, category);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RegularUser_DeniedOnAnyCategory()
    {
        var user = CreateUser(Guid.NewGuid());
        _budgetService.GetEffectiveCoordinatorTeamIdsAsync(Arg.Any<Guid>())
            .Returns(new HashSet<Guid>());
        var category = CreateCategory(CoordinatorTeamId);

        var result = await EvaluateAsync(user, category);

        result.Should().BeFalse();
    }

    // --- Edge cases ---

    [Fact]
    public async Task UnauthenticatedUser_Denied()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var category = CreateCategory(CoordinatorTeamId);

        var result = await EvaluateAsync(user, category);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task UserWithInvalidIdClaim_Denied()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "not-a-guid"),
            new(ClaimTypes.Name, "test@example.com")
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        var category = CreateCategory(CoordinatorTeamId);

        var result = await EvaluateAsync(user, category);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task NullBudgetGroup_CoordinatorStillAllowed()
    {
        // Category with a team but no BudgetGroup navigation loaded
        var user = CreateUser(UserId);
        var category = new BudgetCategory
        {
            Id = Guid.NewGuid(),
            TeamId = CoordinatorTeamId,
            BudgetGroup = null
        };

        var result = await EvaluateAsync(user, category);

        result.Should().BeTrue();
    }

    // --- Helpers ---

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, BudgetCategory resource)
    {
        var requirement = BudgetOperationRequirement.Edit;
        var context = new AuthorizationHandlerContext(
            [requirement], user, resource);

        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static BudgetCategory CreateCategory(
        Guid? teamId,
        bool isRestricted = false,
        bool isDeleted = false)
    {
        return new BudgetCategory
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            BudgetGroup = new BudgetGroup
            {
                Id = Guid.NewGuid(),
                IsRestricted = isRestricted,
                BudgetYear = new BudgetYear
                {
                    Id = Guid.NewGuid(),
                    IsDeleted = isDeleted
                }
            }
        };
    }

    private static ClaimsPrincipal CreateUserWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "admin@example.com")
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static ClaimsPrincipal CreateUser(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "coordinator@example.com")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
