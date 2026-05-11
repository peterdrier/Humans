using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Authorization.UserEmail;
using Humans.Domain.Constants;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Application.Tests.Authorization;

public sealed class UserEmailAuthorizationHandlerTests
{
    private readonly UserEmailAuthorizationHandler _handler = new();

    public static TheoryData<string?, bool> UserEmailAuthorizationCases => new()
    {
        { null, true },
        { RoleNames.Admin, true },
        { RoleNames.HumanAdmin, true },
        { RoleNames.Board, true },
        { "SomeOtherRole", false },
    };

    [HumansTheory]
    [MemberData(nameof(UserEmailAuthorizationCases))]
    public async Task User_email_edit_authorization_matches_expected_scenarios(
        string? actorRole,
        bool expected)
    {
        var actorId = Guid.NewGuid();
        var targetId = actorRole is null ? actorId : Guid.NewGuid();
        var user = actorRole is null
            ? CreateUser(actorId)
            : CreateUser(actorId, actorRole);

        var result = await EvaluateAsync(user, targetId);

        result.Should().Be(expected);
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, Guid targetUserId)
    {
        var requirement = UserEmailOperations.Edit;
        var context = new AuthorizationHandlerContext([requirement], user, targetUserId);

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
