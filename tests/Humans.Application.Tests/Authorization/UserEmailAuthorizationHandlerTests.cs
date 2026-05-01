using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Authorization.UserEmail;
using Humans.Domain.Constants;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Unit tests for UserEmailAuthorizationHandler — self-or-admin gate over
/// a Guid targetUserId resource. Tests cover: actor==target self path,
/// admin override on a different target, denial for unrelated users.
/// </summary>
public sealed class UserEmailAuthorizationHandlerTests
{
    private readonly UserEmailAuthorizationHandler _handler = new();

    [HumansFact]
    public async Task SucceedsWhenActorIsTarget()
    {
        var userId = Guid.NewGuid();
        var user = CreateUser(userId);

        var result = await EvaluateAsync(user, userId);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task SucceedsWhenActorIsAdmin()
    {
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var user = CreateUser(actorId, RoleNames.Admin);

        var result = await EvaluateAsync(user, targetId);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task SucceedsWhenActorIsHumanAdmin()
    {
        // Profiles section invariants list HumanAdmin, Board, Admin as the actors
        // who manage humans via admin pages — admin email management is part of
        // that surface, so the handler grants HumanAdmin on a different target.
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var user = CreateUser(actorId, RoleNames.HumanAdmin);

        var result = await EvaluateAsync(user, targetId);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task SucceedsWhenActorIsBoard()
    {
        // Board is in the same actor list as HumanAdmin/Admin per Profiles.md.
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var user = CreateUser(actorId, RoleNames.Board);

        var result = await EvaluateAsync(user, targetId);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task FailsWhenActorIsNeitherTargetNorAdmin()
    {
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var user = CreateUser(actorId);

        var result = await EvaluateAsync(user, targetId);

        result.Should().BeFalse();
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, Guid targetUserId)
    {
        var requirement = UserEmailOperations.Edit;
        var context = new AuthorizationHandlerContext(
            [requirement], user, targetUserId);

        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsPrincipal CreateUser(Guid userId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "test@example.com")
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
