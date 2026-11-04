using Humans.Users.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Users.Authorization;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.Authorization;

public sealed class UserEmailAuthorizationHandlerTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly HttpContextAccessor _httpContext = new();
    private readonly UserEmailAuthorizationHandler _handler;

    public UserEmailAuthorizationHandlerTests()
    {
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserInfo.Create(new User { Id = call.Arg<Guid>() }, [], [], null));
        _handler = new UserEmailAuthorizationHandler(_users, _httpContext);
    }

    [HumansTheory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task SelfAccess_UsesResolvedActorAndExactTarget(
        bool accountExists, bool targetIsSurvivor, bool edit)
    {
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var targetId = targetIsSurvivor ? survivorId : cookieId;
        _users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(accountExists
            ? UserInfo.Create(new User { Id = survivorId }, [], [], null)
            : null);

        var allowed = await EvaluateAsync(CreateUser(cookieId), targetId,
            edit ? UserEmailOperations.Edit : UserEmailOperations.View);

        allowed.Should().Be(accountExists && targetIsSurvivor);
    }

    [HumansFact]
    public async Task SelfLookup_PropagatesRequestCancellation()
    {
        using var request = new CancellationTokenSource();
        _httpContext.HttpContext = new DefaultHttpContext { RequestAborted = request.Token };
        var actorId = Guid.NewGuid();
        var observed = CancellationToken.None;
        _users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            request.Cancel();
            observed.ThrowIfCancellationRequested();
            return UserInfo.Create(new User { Id = actorId }, [], [], null);
        });

        Func<Task> authorize = () => EvaluateAsync(CreateUser(actorId), actorId);

        await authorize.Should().ThrowAsync<OperationCanceledException>();
        observed.Should().Be(request.Token);
    }

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
