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
    private readonly IBudgetService _budgetService = Substitute.For<IBudgetService>();
    private readonly BudgetAuthorizationHandler _handler;

    private static readonly Guid CoordinatorTeamId = Guid.NewGuid();
    private static readonly Guid OtherTeamId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    public BudgetAuthorizationHandlerTests()
    {
        _handler = new BudgetAuthorizationHandler(_budgetService, new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _budgetService.GetEffectiveCoordinatorTeamIdsAsync(UserId)
            .Returns([CoordinatorTeamId]);
    }

    [HumansFact]
    public async Task AuthorizationRead_PropagatesAbandonedRequests()
    {
        using var request = new CancellationTokenSource();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } };
        var observed = CancellationToken.None;
        _budgetService.GetEffectiveCoordinatorTeamIdsAsync(UserId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            request.Cancel();
            observed.ThrowIfCancellationRequested();
            return new HashSet<Guid> { CoordinatorTeamId };
        });
        var handler = new BudgetAuthorizationHandler(_budgetService, http);
        var context = new AuthorizationHandlerContext([BudgetOperationRequirement.Edit], CreateUser("coordinator"), CreateCategory("coordinator"));
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
        { "coordinator", "coordinator", false, false, false, true, true },
        { "coordinator", "other", false, false, false, true, false },
        { "coordinator", "coordinator", true, false, false, true, false },
        { "coordinator", "coordinator", false, true, false, true, false },
        { "coordinator", "coordinator", false, false, true, true, false },
        { "coordinator", "none", false, false, false, true, false },
        { "regular", "coordinator", false, false, false, true, false },
        { "anonymous", "coordinator", false, false, false, true, false },
        { "invalid-id", "coordinator", false, false, false, true, false },
        { "coordinator", "coordinator", false, false, false, false, true },
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
        if (string.Equals(userKind, "regular", StringComparison.Ordinal))
        {
            _budgetService.GetEffectiveCoordinatorTeamIdsAsync(Arg.Any<Guid>())
                .Returns([]);
        }

        var user = CreateUser(userKind);
        var category = CreateCategory(teamKind, isRestricted, isDeleted, isTicketing, hasBudgetGroup);

        var result = await EvaluateAsync(user, category);

        result.Should().Be(expected);
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, BudgetCategorySnapshot resource)
    {
        var requirement = BudgetOperationRequirement.Edit;
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
            "coordinator" => CoordinatorTeamId,
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
                    groupId,
                    yearId,
                    "Group",
                    isRestricted,
                    isTicketing,
                    new BudgetCategoryYearSnapshot(yearId, "2026", "Budget 2026", isDeleted))
                : null,
            []);
    }

    private static ClaimsPrincipal CreateUser(string kind) =>
        kind switch
        {
            "admin" => CreateUserWithRoles(RoleNames.Admin),
            "finance-admin" => CreateUserWithRoles(FinanceRoles.FinanceAdmin),
            "coordinator" => CreateUserWithId(UserId),
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
            new(ClaimTypes.Name, "coordinator@example.com")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
