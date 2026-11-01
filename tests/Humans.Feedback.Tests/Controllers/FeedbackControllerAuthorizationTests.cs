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
}
