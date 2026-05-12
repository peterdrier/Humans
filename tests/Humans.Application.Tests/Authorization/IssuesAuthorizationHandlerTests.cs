using System.Security.Claims;
using AwesomeAssertions;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Application.Tests.Authorization;

public sealed class IssuesAuthorizationHandlerTests
{
    private readonly IssuesAuthorizationHandler _handler = new();

    public static TheoryData<string[], string?, bool> IssueAuthorizationCases => new()
    {
        { [RoleNames.Admin], IssueSectionRouting.Tickets, true },
        { [RoleNames.Admin], null, true },
        { [RoleNames.TicketAdmin], IssueSectionRouting.Tickets, true },
        { [RoleNames.TicketAdmin], IssueSectionRouting.Scanner, true },
        { [RoleNames.Board], IssueSectionRouting.Scanner, true },
        { [RoleNames.TicketAdmin], IssueSectionRouting.Camps, false },
        { [RoleNames.CampAdmin], IssueSectionRouting.Scanner, false },
        { [RoleNames.CampAdmin], IssueSectionRouting.Camps, true },
        { [RoleNames.CampAdmin], IssueSectionRouting.CityPlanning, true },
        { [RoleNames.ConsentCoordinator], IssueSectionRouting.Onboarding, true },
        { [], IssueSectionRouting.Tickets, false },
        { [RoleNames.TicketAdmin, RoleNames.CampAdmin], null, false },
        { [RoleNames.TicketAdmin], "ZSomeUnknownSection", false },
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
        var issue = CreateIssue(IssueSectionRouting.Tickets);

        (await EvaluateAsync(user, issue)).Should().BeFalse();
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, Issue resource)
    {
        var requirement = IssuesOperationRequirement.Handle;
        var context = new AuthorizationHandlerContext([requirement], user, resource);
        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static Issue CreateIssue(string? section)
    {
        return new Issue
        {
            Id = Guid.NewGuid(),
            ReporterUserId = Guid.NewGuid(),
            Section = section,
            Title = "test",
            Description = "test",
        };
    }

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
