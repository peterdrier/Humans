using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Camps;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Unit tests for CampAuthorizationHandler — resource-based authorization for camp operations.
/// Tests cover: Admin override, CampAdmin override, camp lead access,
/// denial for non-leads, and edge cases.
/// </summary>
public sealed class CampAuthorizationHandlerTests
{
    private readonly ICampService _campService = Substitute.For<ICampService>();
    private readonly CampAuthorizationHandler _handler;

    private static readonly Guid LeadCampId = Guid.NewGuid();
    private static readonly Guid OtherCampId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    public CampAuthorizationHandlerTests()
    {
        _handler = new CampAuthorizationHandler(_campService);

        _campService.IsUserCampLeadAsync(UserId, LeadCampId, Arg.Any<CancellationToken>())
            .Returns(true);
        _campService.IsUserCampLeadAsync(UserId, OtherCampId, Arg.Any<CancellationToken>())
            .Returns(false);
    }

    // --- Admin override ---

    [HumansFact]
    public async Task Admin_CanManageAnyCamp()
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var camp = CreateCamp(OtherCampId);

        var result = await EvaluateAsync(user, camp);

        result.Should().BeTrue();
    }

    // --- CampAdmin override ---

    [HumansFact]
    public async Task CampAdmin_CanManageAnyCamp()
    {
        var user = CreateUserWithRoles(RoleNames.CampAdmin);
        var camp = CreateCamp(OtherCampId);

        var result = await EvaluateAsync(user, camp);

        result.Should().BeTrue();
    }

    // --- Camp lead access ---

    [HumansFact]
    public async Task CampLead_CanManageOwnCamp()
    {
        var user = CreateUser(UserId);
        var camp = CreateCamp(LeadCampId);

        var result = await EvaluateAsync(user, camp);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CampLead_CannotManageOtherCamp()
    {
        var user = CreateUser(UserId);
        var camp = CreateCamp(OtherCampId);

        var result = await EvaluateAsync(user, camp);

        result.Should().BeFalse();
    }

    // --- Denial cases ---

    [HumansFact]
    public async Task RegularUser_DeniedOnAnyCamp()
    {
        var regularUserId = Guid.NewGuid();
        _campService.IsUserCampLeadAsync(regularUserId, LeadCampId, Arg.Any<CancellationToken>())
            .Returns(false);
        var user = CreateUser(regularUserId);
        var camp = CreateCamp(LeadCampId);

        var result = await EvaluateAsync(user, camp);

        result.Should().BeFalse();
    }

    // --- Edge cases ---

    [HumansFact]
    public async Task UnauthenticatedUser_Denied()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var camp = CreateCamp(LeadCampId);

        var result = await EvaluateAsync(user, camp);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task UserWithInvalidIdClaim_Denied()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "not-a-guid"),
            new(ClaimTypes.Name, "test@example.com")
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        var camp = CreateCamp(LeadCampId);

        var result = await EvaluateAsync(user, camp);

        result.Should().BeFalse();
    }

    // --- Helpers ---

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, Camp resource)
    {
        var requirement = CampOperationRequirement.Manage;
        var context = new AuthorizationHandlerContext(
            [requirement], user, resource);

        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static Camp CreateCamp(Guid campId)
    {
        return new Camp
        {
            Id = campId
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
            new(ClaimTypes.Name, "lead@example.com")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
