using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Shifts.Authorization;
using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Humans.Shifts.Tests.Authorization;

/// <summary>
/// Unit tests for <see cref="CampComplianceAccessHandler"/> — moved into this section
/// with the handler (design §15 step 6's asymmetry); the policy it backs
/// (<c>PolicyNames.CampComplianceAccess</c>) is still registered by Shell's
/// AuthorizationPolicyExtensions. Coverage previously lived in
/// Humans.Web.Tests/Authorization/AuthorizationPolicyTests.cs.
/// </summary>
public sealed class CampComplianceAccessHandlerTests
{
    private readonly IShiftManagementServiceRead _shiftManagement = Substitute.For<IShiftManagementServiceRead>();
    private readonly CampComplianceAccessHandler _handler;

    public CampComplianceAccessHandlerTests()
    {
        _handler = new CampComplianceAccessHandler(_shiftManagement);
        _shiftManagement.GetCoordinatorTeamIdsAsync(Arg.Any<Guid>()).Returns([]);
    }

    [HumansTheory]
    [InlineData(RoleNames.CampAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    [InlineData(RoleNames.VolunteerCoordinator, false)]
    public async Task ChecksCorrectRoles(string role, bool expected)
    {
        var result = await EvaluateAsync(CreateUserWithRole(role));
        result.Should().Be(expected);
    }

    [HumansFact]
    public async Task AllowsUserWithCoordinatedTeams()
    {
        var userId = Guid.NewGuid();
        _shiftManagement.GetCoordinatorTeamIdsAsync(userId).Returns([Guid.NewGuid()]);

        var result = await EvaluateAsync(CreateUserWithIdAndRole(userId, "SomeNonAdminRole"));

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task DeniesUserWithNoRoleAndNoCoordinatedTeams()
    {
        var userId = Guid.NewGuid();
        _shiftManagement.GetCoordinatorTeamIdsAsync(userId).Returns([]);

        var result = await EvaluateAsync(CreateUserWithIdAndRole(userId, "SomeNonAdminRole"));

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task CampAdmin_ShortCircuitsWithoutCallingShiftService()
    {
        var result = await EvaluateAsync(CreateUserWithRole(RoleNames.CampAdmin));

        result.Should().BeTrue();
        await _shiftManagement.DidNotReceive().GetCoordinatorTeamIdsAsync(Arg.Any<Guid>());
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user)
    {
        var requirement = new CampComplianceAccessRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsPrincipal CreateUserWithRole(string role) =>
        CreateUserWithIdAndRole(Guid.NewGuid(), role);

    private static ClaimsPrincipal CreateUserWithIdAndRole(Guid userId, string role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "testuser@example.com"),
            new(ClaimTypes.Role, role)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
