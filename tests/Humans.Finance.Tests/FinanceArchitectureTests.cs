using System.Reflection;
using AwesomeAssertions;
using Humans.Finance.Contracts;
using Humans.Finance.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Finance.Tests;

public class FinanceArchitectureTests
{
    [HumansFact]
    public void FinanceControllerRequiresFinanceRead()
    {
        // Nothing proves the negative at runtime: the render tests only ever sign in as Admin.
        typeof(FinanceController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single().Policy
            .Should().Be(FinancePolicies.Read);
    }

    [HumansFact]
    public void EveryFinanceControllerPost_RequiresFinanceManage()
    {
        // Board passes the class-level Read policy, so a POST that forgot its own policy would let Board write.
        var posts = typeof(FinanceController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null)
            .ToList();

        posts.Should().NotBeEmpty();
        foreach (var post in posts)
        {
            var attribute = post.GetCustomAttribute<AuthorizeAttribute>(inherit: false);
            attribute.Should().NotBeNull(because: $"POST {post.Name} must not ride on the read policy");
            attribute.Policy.Should().Be(FinancePolicies.Manage, because: $"POST {post.Name} must not ride on the read policy");
        }
    }
}
