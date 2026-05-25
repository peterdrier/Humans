using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Models;
using Humans.Application.Services.Cantina;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using NSubstitute;

namespace Humans.Application.Tests.Services.Cantina;

/// <summary>
/// Unit tests for <see cref="CantinaAccessService"/>. The role-based OR-chain
/// is verified first, then the team-membership fallback (case-insensitive
/// substring on "Cantina"), then the defensive paths for missing claims.
/// </summary>
public class CantinaAccessServiceTests
{
    private readonly ITeamService _teamService;
    private readonly CantinaAccessService _service;

    public CantinaAccessServiceTests()
    {
        _teamService = Substitute.For<ITeamService>();
        _service = new CantinaAccessService(_teamService);
    }

    [HumansFact]
    public async Task CanView_AdminRole_ReturnsTrue()
    {
        var principal = BuildPrincipal(Guid.NewGuid(), RoleNames.Admin);

        var result = await _service.CanViewRosterAsync(principal);

        result.Should().BeTrue();
        await _teamService.DidNotReceive().GetActiveTeamMembershipsForUserAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CanView_NoInfoAdminRole_ReturnsTrue()
    {
        var principal = BuildPrincipal(Guid.NewGuid(), RoleNames.NoInfoAdmin);

        var result = await _service.CanViewRosterAsync(principal);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CanView_VolunteerCoordinatorRole_ReturnsTrue()
    {
        var principal = BuildPrincipal(Guid.NewGuid(), RoleNames.VolunteerCoordinator);

        var result = await _service.CanViewRosterAsync(principal);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CanView_NoRoleButInCantinaTeam_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var principal = BuildPrincipal(userId);
        _teamService.GetActiveTeamMembershipsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TeamMembership>>(new[]
            {
                new TeamMembership("Cantina Crew", TeamMemberRole.Member)
            }));

        var result = await _service.CanViewRosterAsync(principal);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CanView_NoRoleButInLowercaseCantinaTeam_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var principal = BuildPrincipal(userId);
        _teamService.GetActiveTeamMembershipsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TeamMembership>>(new[]
            {
                new TeamMembership("cantina prep", TeamMemberRole.Member)
            }));

        var result = await _service.CanViewRosterAsync(principal);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CanView_NoRoleNoCantinaTeam_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var principal = BuildPrincipal(userId);
        _teamService.GetActiveTeamMembershipsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TeamMembership>>(new[]
            {
                new TeamMembership("Volunteers", TeamMemberRole.Member),
                new TeamMembership("Power Build Crew", TeamMemberRole.Member)
            }));

        var result = await _service.CanViewRosterAsync(principal);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task CanView_PrincipalMissingNameIdentifier_ReturnsFalse()
    {
        // No NameIdentifier claim, no privileged roles. Fails closed.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "TestAuth"));

        var result = await _service.CanViewRosterAsync(principal);

        result.Should().BeFalse();
        await _teamService.DidNotReceive().GetActiveTeamMembershipsForUserAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CanView_AdminShortCircuitsBeforeTeamQuery()
    {
        // Admin role is set — verify the cheap role check fires before any DB call.
        var principal = BuildPrincipal(Guid.NewGuid(), RoleNames.Admin);

        var result = await _service.CanViewRosterAsync(principal);

        result.Should().BeTrue();
        await _teamService.DidNotReceive().GetActiveTeamMembershipsForUserAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ---- helpers ----

    private static ClaimsPrincipal BuildPrincipal(Guid userId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString())
        };
        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
