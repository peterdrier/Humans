using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Permissions.Contracts;
using Humans.Base.Authorization;
using Humans.Finance.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Budget.Contracts;
using Humans.Base.Constants;
using Humans.Budget.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Xunit;

namespace Humans.Budget.Tests.Authorization;

public sealed class BudgetAuthorizationHandlerTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();
    private readonly BudgetAuthorizationHandler _handler;

    private static readonly Guid EditorTeamId = Guid.NewGuid();
    private static readonly Guid OtherTeamId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    public BudgetAuthorizationHandlerTests()
    {
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserInfo.Create(new User { Id = call.Arg<Guid>() }, [], [], null));
        _handler = new BudgetAuthorizationHandler(_permissions, _users, new HttpContextAccessor());
        Holds(UserId);
    }

    [HumansTheory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    public async Task MembershipAccess_UsesResolvedAccountAndDeniesMissingAccounts(
        bool accountExists, bool survivorAuthorized, bool viewing)
    {
        var cookieId = survivorAuthorized ? Guid.NewGuid() : UserId;
        var survivorId = survivorAuthorized ? UserId : Guid.NewGuid();
        _users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(accountExists
            ? UserInfo.Create(new User { Id = survivorId }, [], [], null)
            : null);

        var allowed = await EvaluateAsync(CreateUserWithId(cookieId), CreateCategory("editor"),
            viewing ? BudgetOperationRequirement.View : BudgetOperationRequirement.Edit);

        allowed.Should().Be(accountExists && survivorAuthorized);
    }

    [HumansTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task AuthorizationRead_PropagatesAbandonedRequests(bool atUser, bool viewing)
    {
        using var request = new CancellationTokenSource();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } };
        var observed = CancellationToken.None;
        _users.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            var ct = call.Arg<CancellationToken>();
            if (atUser)
            {
                observed = ct;
                request.Cancel();
            }
            ct.ThrowIfCancellationRequested();
            return UserInfo.Create(new User { Id = UserId }, [], [], null);
        });
        bool Permission(CancellationToken ct)
        {
            observed = ct;
            if (!atUser) request.Cancel();
            ct.ThrowIfCancellationRequested();
            return true;
        }
        _permissions.HasAsync(UserId, BudgetPermissions.Edit, Arg.Any<Owner>(), Arg.Any<CancellationToken>())
            .Returns(call => Permission(call.Arg<CancellationToken>()));
        _permissions.GetOwnersAsync(UserId, BudgetPermissions.Edit, Arg.Any<CancellationToken>())
            .Returns(call => Permission(call.Arg<CancellationToken>())
                ? [new Owner(TeamOwner.Kind, EditorTeamId)]
                : []);
        var handler = new BudgetAuthorizationHandler(_permissions, _users, http);
        var requirement = viewing ? BudgetOperationRequirement.View : BudgetOperationRequirement.Edit;
        var context = new AuthorizationHandlerContext([requirement], CreateUser("editor"), CreateCategory("editor"));
        Func<Task> authorize = () => handler.HandleAsync(context);
        await authorize.Should().ThrowAsync<OperationCanceledException>();
        observed.Should().Be(request.Token);
        context.HasSucceeded.Should().BeFalse();
    }

    public static TheoryData<string, string, bool, bool, bool, bool, bool> BudgetAuthorizationCases => new()
    {
        { "admin", "other", false, false, false, true, true },
        { "admin", "other", true, false, false, true, true },
        { "admin", "other", false, true, false, true, true },
        { "finance-admin", "other", false, false, false, true, true },
        { "finance-admin", "other", true, false, false, true, true },
        { "finance-admin", "other", false, false, true, true, true },
        { "finance-admin", "none", false, false, false, true, true },
        { "editor", "editor", false, false, false, true, true },
        { "editor", "other", false, false, false, true, false },
        { "editor", "editor", true, false, false, true, false },
        { "editor", "editor", false, true, false, true, false },
        { "editor", "editor", false, false, true, true, false },
        { "editor", "none", false, false, false, true, false },
        { "regular", "editor", false, false, false, true, false },
        { "anonymous", "editor", false, false, false, true, false },
        { "invalid-id", "editor", false, false, false, true, false },
        { "editor", "editor", false, false, false, false, true },
    };

    [HumansTheory]
    [MemberData(nameof(BudgetAuthorizationCases))]
    public async Task Budget_edit_authorization_matches_expected_scenarios(
        string userKind,
        string teamKind,
        bool isRestricted,
        bool isDeleted,
        bool isTicketing,
        bool hasBudgetGroup,
        bool expected)
    {
        var user = CreateUser(userKind);
        var category = CreateCategory(teamKind, isRestricted, isDeleted, isTicketing, hasBudgetGroup);

        var result = await EvaluateAsync(user, category);

        result.Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("finance-admin", "none", true, true, true, true)]
    [InlineData("admin", "none", true, true, true, true)]
    [InlineData("editor", "editor", false, false, false, true)]
    [InlineData("editor", "other", false, false, false, true)]
    [InlineData("editor", "none", false, false, false, true)]
    [InlineData("editor", "other", false, true, false, true)]
    [InlineData("editor", "editor", true, false, false, false)]
    [InlineData("editor", "editor", false, false, true, false)]
    [InlineData("regular", "editor", false, false, false, false)]
    [InlineData("anonymous", "editor", false, false, false, false)]
    [InlineData("invalid-id", "editor", false, false, false, false)]
    public async Task View_KeepsEditorReadScope_WithoutEditAccess(
        string userKind, string teamKind, bool restricted, bool deleted, bool ticketing, bool expected)
    {
        var allowed = await EvaluateAsync(CreateUser(userKind),
            CreateCategory(teamKind, restricted, deleted, ticketing), BudgetOperationRequirement.View);

        allowed.Should().Be(expected);
    }

    private void Holds(Guid userId)
    {
        _permissions.HasAsync(userId, BudgetPermissions.Edit, new Owner(TeamOwner.Kind, EditorTeamId), Arg.Any<CancellationToken>())
            .Returns(true);
        _permissions.GetOwnersAsync(userId, BudgetPermissions.Edit, Arg.Any<CancellationToken>())
            .Returns([new Owner(TeamOwner.Kind, EditorTeamId)]);
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, BudgetCategorySnapshot resource, BudgetOperationRequirement? requirement = null)
    {
        requirement ??= BudgetOperationRequirement.Edit;
        var context = new AuthorizationHandlerContext([requirement], user, resource);

        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static BudgetCategorySnapshot CreateCategory(
        string teamKind,
        bool isRestricted = false,
        bool isDeleted = false,
        bool isTicketing = false,
        bool hasBudgetGroup = true)
    {
        Guid? teamId = teamKind switch
        {
            "editor" => EditorTeamId,
            "other" => OtherTeamId,
            "none" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(teamKind), teamKind, null)
        };

        var groupId = Guid.NewGuid();
        var yearId = Guid.NewGuid();

        return new BudgetCategorySnapshot(
            Guid.NewGuid(),
            groupId,
            "Category",
            0m,
            default,
            teamId,
            0,
            hasBudgetGroup
                ? new BudgetCategoryGroupSnapshot(
                    yearId,
                    "Group",
                    isRestricted,
                    isTicketing,
                    new BudgetCategoryYearSnapshot(yearId, "Budget 2026", isDeleted))
                : null,
            []);
    }

    private static ClaimsPrincipal CreateUser(string kind) =>
        kind switch
        {
            "admin" => CreateUserWithRoles(RoleNames.Admin),
            "finance-admin" => CreateUserWithRoles(FinanceRoles.FinanceAdmin),
            "editor" => CreateUserWithId(UserId),
            "regular" => CreateUserWithId(Guid.NewGuid()),
            "anonymous" => new ClaimsPrincipal(new ClaimsIdentity()),
            "invalid-id" => new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "not-a-guid"),
                new Claim(ClaimTypes.Name, "test@example.com")
            ], "TestAuth")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

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

    private static ClaimsPrincipal CreateUserWithId(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "editor@example.com")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
