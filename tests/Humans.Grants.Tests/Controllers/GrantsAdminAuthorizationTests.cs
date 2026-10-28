using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Grants.Authorization;
using Humans.Grants.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Grants.Tests.Controllers;

/// <summary>
/// Pins each data-modifying admin action to its own policy: program administration is Board or
/// Admin (a program manager is denied by the attribute, not by a body check), grant actions name
/// the manage policy and keep the per-program resource check.
/// </summary>
public sealed class GrantsAdminAuthorizationTests
{
    private static IEnumerable<MethodInfo> Actions(string name) =>
        typeof(GrantsAdminController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => string.Equals(m.Name, name, StringComparison.Ordinal));

    private static void AssertPolicy(string action, string policy, int overloads)
    {
        var methods = Actions(action).ToList();
        methods.Should().HaveCount(overloads);
        foreach (var method in methods)
        {
            var attribute = method.GetCustomAttribute<AuthorizeAttribute>(inherit: false);
            attribute.Should().NotBeNull($"{action} must name its policy");
            attribute.Policy.Should().Be(policy);
        }
    }

    [HumansFact]
    public void ProgramAdministration_RequiresBoardOrAdmin()
    {
        AssertPolicy(nameof(GrantsAdminController.New), PolicyNames.BoardOrAdmin, overloads: 2);
        AssertPolicy(nameof(GrantsAdminController.Edit), PolicyNames.BoardOrAdmin, overloads: 2);
    }

    [HumansFact]
    public void GrantActions_RequireTheManagePolicy()
    {
        AssertPolicy(nameof(GrantsAdminController.Issue), GrantsPolicies.Manage, overloads: 1);
        AssertPolicy(nameof(GrantsAdminController.Revoke), GrantsPolicies.Manage, overloads: 1);
        AssertPolicy(nameof(GrantsAdminController.Resend), GrantsPolicies.Manage, overloads: 1);
    }

    [HumansFact]
    public void EveryPost_NamesAPolicy_AndValidatesTheAntiForgeryToken()
    {
        var posts = typeof(GrantsAdminController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null)
            .ToList();

        posts.Should().NotBeEmpty();
        foreach (var post in posts)
        {
            var attribute = post.GetCustomAttribute<AuthorizeAttribute>(inherit: false);
            attribute.Should().NotBeNull($"{post.Name} modifies data");
            attribute.Policy.Should().NotBeNullOrEmpty($"{post.Name} modifies data");
            post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull($"{post.Name} is a POST");
        }
    }
}
