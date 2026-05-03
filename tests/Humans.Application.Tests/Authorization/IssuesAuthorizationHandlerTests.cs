using System.Security.Claims;
using AwesomeAssertions;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Application.Tests.Authorization;

/// <summary>
/// Unit tests for IssuesAuthorizationHandler — resource-based authorization for
/// issue handler operations. Tests cover: Admin override, section role-holder
/// access (including the multi-role Onboarding case), denial for non-handlers,
/// null-section (Admin-only) issues, and unknown-section issues.
/// </summary>
public sealed class IssuesAuthorizationHandlerTests
{
    private readonly IssuesAuthorizationHandler _handler = new();

    [HumansFact]
    public async Task Admin_CanHandleAnyIssue()
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var issue = CreateIssue(IssueSectionRouting.Tickets);

        (await EvaluateAsync(user, issue)).Should().BeTrue();
    }

    [HumansFact]
    public async Task Admin_CanHandleNullSectionIssue()
    {
        var user = CreateUserWithRoles(RoleNames.Admin);
        var issue = CreateIssue(section: null);

        (await EvaluateAsync(user, issue)).Should().BeTrue();
    }

    [HumansFact]
    public async Task TicketAdmin_CanHandleTicketsSectionIssue()
    {
        var user = CreateUserWithRoles(RoleNames.TicketAdmin);
        var issue = CreateIssue(IssueSectionRouting.Tickets);

        (await EvaluateAsync(user, issue)).Should().BeTrue();
    }

    [HumansFact]
    public async Task TicketAdmin_CannotHandleCampsSectionIssue()
    {
        var user = CreateUserWithRoles(RoleNames.TicketAdmin);
        var issue = CreateIssue(IssueSectionRouting.Camps);

        (await EvaluateAsync(user, issue)).Should().BeFalse();
    }

    [HumansFact]
    public async Task CampAdmin_CanHandleCampsAndCityPlanningIssues()
    {
        var user = CreateUserWithRoles(RoleNames.CampAdmin);

        (await EvaluateAsync(user, CreateIssue(IssueSectionRouting.Camps))).Should().BeTrue();
        (await EvaluateAsync(user, CreateIssue(IssueSectionRouting.CityPlanning))).Should().BeTrue();
    }

    [HumansFact]
    public async Task ConsentCoordinator_CanHandleOnboardingIssue()
    {
        // Onboarding maps to multiple roles — verify ANY of them grants access.
        var user = CreateUserWithRoles(RoleNames.ConsentCoordinator);
        var issue = CreateIssue(IssueSectionRouting.Onboarding);

        (await EvaluateAsync(user, issue)).Should().BeTrue();
    }

    [HumansFact]
    public async Task RegularUser_CannotHandleAnyIssue()
    {
        var user = CreateUserWithRoles(); // no roles
        var issue = CreateIssue(IssueSectionRouting.Tickets);

        (await EvaluateAsync(user, issue)).Should().BeFalse();
    }

    [HumansFact]
    public async Task NonAdmin_CannotHandleNullSectionIssue()
    {
        // Null section maps to no roles → Admin-only.
        var user = CreateUserWithRoles(RoleNames.TicketAdmin, RoleNames.CampAdmin);
        var issue = CreateIssue(section: null);

        (await EvaluateAsync(user, issue)).Should().BeFalse();
    }

    [HumansFact]
    public async Task NonAdmin_CannotHandleUnknownSectionIssue()
    {
        var user = CreateUserWithRoles(RoleNames.TicketAdmin);
        var issue = CreateIssue("ZSomeUnknownSection");

        (await EvaluateAsync(user, issue)).Should().BeFalse();
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
