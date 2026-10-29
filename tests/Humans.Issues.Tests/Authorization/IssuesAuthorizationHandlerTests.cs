using Humans.Users.Contracts;
using Humans.Camps.Contracts;
using Humans.Tickets.Contracts;
using Humans.Consent.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Issues.Authorization;
using Humans.Issues.Contracts;
using Microsoft.AspNetCore.Authorization;
using NodaTime;
using Xunit;

namespace Humans.Issues.Tests.Authorization;

public sealed class IssuesAuthorizationHandlerTests
{
    private readonly IssuesAuthorizationHandler _handler = new(Tests.Domain.TestIssueQueues.Shipped());

    public static TheoryData<string[], string?, bool> IssueAuthorizationCases => new()
    {
        { [RoleNames.Admin], "Tickets", true },
        { [RoleNames.Admin], null, true },
        { [TicketsRoles.TicketsAdmin], "Tickets", true },
        { [TicketsRoles.TicketsAdmin], "Scanner", true },
        { [RoleNames.Board], "Scanner", true },
        { [TicketsRoles.TicketsAdmin], "Camps", false },
        { [CampsRoles.CampsAdmin], "Scanner", false },
        { [CampsRoles.CampsAdmin], "Camps", true },
        { [CampsRoles.CampsAdmin], "CityPlanning", true },
        { [ConsentRoles.ConsentCoordinator], "Onboarding", true },
        { [], "Tickets", false },
        { [TicketsRoles.TicketsAdmin, CampsRoles.CampsAdmin], null, false },
        { [TicketsRoles.TicketsAdmin], "ZSomeUnknownSection", false },
        // Keys that outlived their section names: Users declares Profiles, Consent declares Legal.
        { [UsersRoles.UsersAdmin], "Profiles", true },
        { [ConsentRoles.ConsentCoordinator], "Legal", true },
        { [TicketsRoles.TicketsAdmin], "Profiles", false },
    };

    [HumansTheory]
    [MemberData(nameof(IssueAuthorizationCases))]
    public async Task Issue_handle_authorization_matches_expected_scenarios(
        string[] roles,
        string? section,
        bool expected)
    {
        var user = CreateUserWithRoles(roles);
        var issue = CreateIssue(section);

        (await EvaluateAsync(user, issue)).Should().Be(expected);
    }

    [HumansFact]
    public async Task UnauthenticatedUser_Denied()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var issue = CreateIssue("Tickets");

        (await EvaluateAsync(user, issue)).Should().BeFalse();
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, IssueDetail resource)
    {
        var requirement = IssuesOperationRequirement.Handle;
        var context = new AuthorizationHandlerContext([requirement], user, resource);
        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static IssueDetail CreateIssue(string? section) => new(
        Id: Guid.NewGuid(),
        Status: IssueStatus.Triage,
        Category: IssueCategory.Bug,
        Section: section,
        Title: "test",
        Description: "test",
        PageUrl: null,
        UserAgent: null,
        AdditionalContext: null,
        ScreenshotStoragePath: null,
        ReporterUserId: Guid.NewGuid(),
        AssigneeUserId: null,
        ResolvedByUserId: null,
        GitHubIssueNumber: null,
        DueDate: null,
        CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
        ResolvedAt: null,
        CommentCount: 0);

    private static ClaimsPrincipal CreateUserWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "user@example.com"),
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
