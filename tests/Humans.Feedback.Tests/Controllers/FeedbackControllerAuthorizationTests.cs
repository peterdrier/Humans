using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Feedback.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Feedback.Tests.Controllers;

public class FeedbackControllerAuthorizationTests
{
    [HumansFact]
    public void Controller_is_gated_on_AdminOnly()
    {
        var attr = typeof(FeedbackController).GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(PolicyNames.AdminOnly,
            because: "Feedback is retired: FeedbackAdmin alone and reporters reach none of it");
    }

    [HumansFact]
    public void No_action_widens_or_bypasses_the_controller_gate()
    {
        var actions = typeof(FeedbackController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        actions.Should().NotBeEmpty();
        foreach (var action in actions)
        {
            action.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull(because: action.Name);
            action.GetCustomAttributes<AuthorizeAttribute>().Should().BeEmpty(because: action.Name);
        }
    }
}
