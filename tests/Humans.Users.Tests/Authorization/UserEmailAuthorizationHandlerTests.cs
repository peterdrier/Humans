using Humans.Users.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Users.Authorization;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Users.Tests.Authorization;

public sealed class UserEmailAuthorizationHandlerTests
{
    private readonly UserEmailAuthorizationHandler _handler = new();

    public static TheoryData<string?, bool> UserEmailAuthorizationCases => new()
    {
        { null, true },
        { RoleNames.Admin, true },
        { UsersRoles.UsersAdmin, true },
        { RoleNames.Board, false },
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

    [HumansTheory]
    [InlineData(RoleNames.Board, true)]
    [InlineData(UsersRoles.UsersAdmin, true)]
    [InlineData("SomeOtherRole", false)]
    public async Task Board_views_another_members_emails_but_never_edits_them(string actorRole, bool expected)
    {
        var user = CreateUser(Guid.NewGuid(), actorRole);

        var result = await EvaluateAsync(user, Guid.NewGuid(), UserEmailOperations.View);

        result.Should().Be(expected);
    }

    private async Task<bool> EvaluateAsync(
        ClaimsPrincipal user, Guid targetUserId, UserEmailOperationRequirement? requirement = null)
    {
        requirement ??= UserEmailOperations.Edit;
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
