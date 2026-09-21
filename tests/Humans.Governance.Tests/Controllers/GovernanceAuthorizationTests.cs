using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Governance.Controllers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Governance.Tests.Controllers;

/// <summary>
/// Pins the Governance controller policy boundaries, including the narrower actions that
/// override their Board-or-Admin controller defaults.
/// </summary>
public sealed class GovernanceAuthorizationTests
{
    [HumansFact]
    public void Application_admin_views_require_BoardOrAdmin()
    {
        AssertActionPolicy<GovernanceApplicationsController>(nameof(GovernanceApplicationsController.Admin), PolicyNames.BoardOrAdmin);
        AssertActionPolicy<GovernanceApplicationsController>(nameof(GovernanceApplicationsController.AdminDetail), PolicyNames.BoardOrAdmin);
    }

    [HumansFact]
    public void Board_voting_actions_have_their_intended_policy_boundaries()
    {
        AssertControllerPolicy<GovernanceBoardVotingController>(PolicyNames.BoardOrAdmin);
        AssertActionPolicy<GovernanceBoardVotingController>(nameof(GovernanceBoardVotingController.Vote), PolicyNames.BoardOnly);
        AssertActionPolicy<GovernanceBoardVotingController>(nameof(GovernanceBoardVotingController.Finalize), PolicyNames.AdminOnly);
    }

    [HumansFact]
    public void Assembly_vote_lifecycle_and_peek_require_AdminOnly()
    {
        AssertControllerPolicy<GovernanceVotesAdminController>(PolicyNames.BoardOrAdmin);

        foreach (var action in new[]
                 {
                     nameof(GovernanceVotesAdminController.Open),
                     nameof(GovernanceVotesAdminController.Stop),
                     nameof(GovernanceVotesAdminController.Extend),
                     nameof(GovernanceVotesAdminController.Cancel),
                     nameof(GovernanceVotesAdminController.Peek)
                 })
            AssertActionPolicy<GovernanceVotesAdminController>(action, PolicyNames.AdminOnly);
    }

    private static void AssertControllerPolicy<TController>(string expectedPolicy)
    {
        var attribute = typeof(TController).GetCustomAttribute<AuthorizeAttribute>();
        attribute.Should().NotBeNull($"{typeof(TController).Name} must have [Authorize]");
        attribute!.Policy.Should().Be(expectedPolicy);
    }

    private static void AssertActionPolicy<TController>(string actionName, string expectedPolicy)
    {
        var action = typeof(TController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Single(method => string.Equals(method.Name, actionName, StringComparison.Ordinal));
        var attribute = action.GetCustomAttribute<AuthorizeAttribute>();

        attribute.Should().NotBeNull($"{typeof(TController).Name}.{actionName} must override its controller policy");
        attribute!.Policy.Should().Be(expectedPolicy);
    }
}
