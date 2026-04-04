using System.Security.Claims;
using AwesomeAssertions;
using Humans.Domain.Constants;
using Humans.Web.Authorization;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Verifies that each registered authorization policy allows the correct roles
/// and denies others. Uses the real ASP.NET Core authorization pipeline.
/// </summary>
public class AuthorizationPolicyTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IAuthorizationService _authorizationService;

    public AuthorizationPolicyTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHumansAuthorizationPolicies();
        _serviceProvider = services.BuildServiceProvider();
        _authorizationService = _serviceProvider.GetRequiredService<IAuthorizationService>();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    // --- AdminOnly ---

    [Theory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    [InlineData(RoleNames.HumanAdmin, false)]
    [InlineData(RoleNames.FinanceAdmin, false)]
    public async Task AdminOnly_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.AdminOnly, role);
        result.Succeeded.Should().Be(expected);
    }

    [Fact]
    public async Task AdminOnly_DeniesUnauthenticated()
    {
        var result = await AuthorizeAnonymousAsync(PolicyNames.AdminOnly);
        result.Succeeded.Should().BeFalse();
    }

    // --- BoardOnly ---

    [Theory]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, false)]
    [InlineData(RoleNames.HumanAdmin, false)]
    public async Task BoardOnly_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.BoardOnly, role);
        result.Succeeded.Should().Be(expected);
    }

    [Fact]
    public async Task BoardOnly_DeniesUnauthenticated()
    {
        var result = await AuthorizeAnonymousAsync(PolicyNames.BoardOnly);
        result.Succeeded.Should().BeFalse();
    }

    // --- BoardOrAdmin ---

    [Theory]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    [InlineData(RoleNames.HumanAdmin, false)]
    [InlineData(RoleNames.VolunteerCoordinator, false)]
    public async Task BoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.BoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- HumanAdminBoardOrAdmin ---

    [Theory]
    [InlineData(RoleNames.HumanAdmin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    [InlineData(RoleNames.FinanceAdmin, false)]
    public async Task HumanAdminBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.HumanAdminBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- HumanAdminOrAdmin ---

    [Theory]
    [InlineData(RoleNames.HumanAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task HumanAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.HumanAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- TeamsAdminBoardOrAdmin ---

    [Theory]
    [InlineData(RoleNames.TeamsAdmin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.HumanAdmin, false)]
    [InlineData(RoleNames.CampAdmin, false)]
    public async Task TeamsAdminBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.TeamsAdminBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- CampAdminOrAdmin ---

    [Theory]
    [InlineData(RoleNames.CampAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task CampAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.CampAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- TicketAdminBoardOrAdmin ---

    [Theory]
    [InlineData(RoleNames.TicketAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    [InlineData(RoleNames.HumanAdmin, false)]
    public async Task TicketAdminBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.TicketAdminBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- TicketAdminOrAdmin ---

    [Theory]
    [InlineData(RoleNames.TicketAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task TicketAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.TicketAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- FeedbackAdminOrAdmin ---

    [Theory]
    [InlineData(RoleNames.FeedbackAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task FeedbackAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.FeedbackAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- FinanceAdminOrAdmin ---

    [Theory]
    [InlineData(RoleNames.FinanceAdmin, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task FinanceAdminOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.FinanceAdminOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- ReviewQueueAccess ---

    [Theory]
    [InlineData(RoleNames.ConsentCoordinator, true)]
    [InlineData(RoleNames.VolunteerCoordinator, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    [InlineData(RoleNames.HumanAdmin, false)]
    public async Task ReviewQueueAccess_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.ReviewQueueAccess, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- ConsentCoordinatorBoardOrAdmin ---

    [Theory]
    [InlineData(RoleNames.ConsentCoordinator, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.VolunteerCoordinator, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task ConsentCoordinatorBoardOrAdmin_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.ConsentCoordinatorBoardOrAdmin, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- ShiftDashboardAccess ---

    [Theory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.NoInfoAdmin, true)]
    [InlineData(RoleNames.VolunteerCoordinator, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task ShiftDashboardAccess_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.ShiftDashboardAccess, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- ShiftDepartmentManager ---

    [Theory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.NoInfoAdmin, true)]
    [InlineData(RoleNames.VolunteerCoordinator, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.TeamsAdmin, false)]
    public async Task ShiftDepartmentManager_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.ShiftDepartmentManager, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- PrivilegedSignupApprover ---

    [Theory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.NoInfoAdmin, true)]
    [InlineData(RoleNames.VolunteerCoordinator, false)]
    [InlineData(RoleNames.Board, false)]
    public async Task PrivilegedSignupApprover_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.PrivilegedSignupApprover, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- VolunteerManager ---

    [Theory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.VolunteerCoordinator, true)]
    [InlineData(RoleNames.NoInfoAdmin, false)]
    [InlineData(RoleNames.Board, false)]
    public async Task VolunteerManager_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.VolunteerManager, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- VolunteerSectionAccess ---

    [Theory]
    [InlineData(RoleNames.TeamsAdmin, true)]
    [InlineData(RoleNames.Board, true)]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.VolunteerCoordinator, true)]
    [InlineData(RoleNames.NoInfoAdmin, false)]
    [InlineData(RoleNames.CampAdmin, false)]
    public async Task VolunteerSectionAccess_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.VolunteerSectionAccess, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- MedicalDataViewer ---

    [Theory]
    [InlineData(RoleNames.Admin, true)]
    [InlineData(RoleNames.NoInfoAdmin, true)]
    [InlineData(RoleNames.Board, false)]
    [InlineData(RoleNames.VolunteerCoordinator, false)]
    public async Task MedicalDataViewer_ChecksCorrectRoles(string role, bool expected)
    {
        var result = await AuthorizeAsync(PolicyNames.MedicalDataViewer, role);
        result.Succeeded.Should().Be(expected);
    }

    // --- ActiveMemberOrShiftAccess (composite) ---

    [Fact]
    public async Task ActiveMemberOrShiftAccess_AllowsActiveMember()
    {
        var user = CreateUserWithClaim(
            RoleAssignmentClaimsTransformation.ActiveMemberClaimType,
            RoleAssignmentClaimsTransformation.ActiveClaimValue);
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.ActiveMemberOrShiftAccess);
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Board)]
    [InlineData(RoleNames.TeamsAdmin)]
    public async Task ActiveMemberOrShiftAccess_AllowsTeamsAdminBoardOrAdmin(string role)
    {
        var result = await AuthorizeAsync(PolicyNames.ActiveMemberOrShiftAccess, role);
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(RoleNames.NoInfoAdmin)]
    [InlineData(RoleNames.VolunteerCoordinator)]
    public async Task ActiveMemberOrShiftAccess_AllowsShiftDashboardRoles(string role)
    {
        var result = await AuthorizeAsync(PolicyNames.ActiveMemberOrShiftAccess, role);
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ActiveMemberOrShiftAccess_DeniesRegularUser()
    {
        var user = CreateAuthenticatedUser();
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.ActiveMemberOrShiftAccess);
        result.Succeeded.Should().BeFalse();
    }

    // --- IsActiveMember (composite) ---

    [Fact]
    public async Task IsActiveMember_AllowsActiveMemberClaim()
    {
        var user = CreateUserWithClaim(
            RoleAssignmentClaimsTransformation.ActiveMemberClaimType,
            RoleAssignmentClaimsTransformation.ActiveClaimValue);
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.IsActiveMember);
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Board)]
    [InlineData(RoleNames.TeamsAdmin)]
    public async Task IsActiveMember_AllowsTeamsAdminBoardOrAdmin(string role)
    {
        var result = await AuthorizeAsync(PolicyNames.IsActiveMember, role);
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(RoleNames.NoInfoAdmin)]
    [InlineData(RoleNames.CampAdmin)]
    public async Task IsActiveMember_DeniesNonMatchingRoles(string role)
    {
        var result = await AuthorizeAsync(PolicyNames.IsActiveMember, role);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task IsActiveMember_DeniesRegularUser()
    {
        var user = CreateAuthenticatedUser();
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.IsActiveMember);
        result.Succeeded.Should().BeFalse();
    }

    // --- HumanAdminOnly (composite) ---

    [Fact]
    public async Task HumanAdminOnly_AllowsHumanAdminWithoutBoardOrAdmin()
    {
        var result = await AuthorizeAsync(PolicyNames.HumanAdminOnly, RoleNames.HumanAdmin);
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task HumanAdminOnly_DeniesHumanAdminWhoIsAlsoAdmin()
    {
        var user = CreateUserWithRoles(RoleNames.HumanAdmin, RoleNames.Admin);
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.HumanAdminOnly);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task HumanAdminOnly_DeniesHumanAdminWhoIsAlsoBoard()
    {
        var user = CreateUserWithRoles(RoleNames.HumanAdmin, RoleNames.Board);
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.HumanAdminOnly);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task HumanAdminOnly_DeniesPlainAdmin()
    {
        var result = await AuthorizeAsync(PolicyNames.HumanAdminOnly, RoleNames.Admin);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task HumanAdminOnly_DeniesRegularUser()
    {
        var user = CreateAuthenticatedUser();
        var result = await _authorizationService.AuthorizeAsync(user, PolicyNames.HumanAdminOnly);
        result.Succeeded.Should().BeFalse();
    }

    // --- Unknown policy fails closed ---

    [Fact]
    public async Task UnknownPolicy_ThrowsInvalidOperationException()
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var act = () => _authorizationService.AuthorizeAsync(user, "NonExistentPolicy");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // --- Helpers ---

    private async Task<AuthorizationResult> AuthorizeAsync(string policyName, string role)
    {
        var user = CreateUserWithRoles(role);
        return await _authorizationService.AuthorizeAsync(user, policyName);
    }

    private async Task<AuthorizationResult> AuthorizeAnonymousAsync(string policyName)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        return await _authorizationService.AuthorizeAsync(user, policyName);
    }

    private static ClaimsPrincipal CreateUserWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "testuser@example.com")
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreateAuthenticatedUser()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "testuser@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreateUserWithClaim(string claimType, string claimValue)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "testuser@example.com"),
            new(claimType, claimValue)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }
}
