using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;

namespace Humans.Issues.Tests.Domain;

/// <summary>A section's queue declaration, stood up by hand.</summary>
internal sealed record TestQueueOwner(string QueueKey, IReadOnlyList<string> OwningRoles) : IIssueQueueOwner;

/// <summary>
/// The contributions the shipped sections declare through <see cref="IIssueQueueOwner"/>. Issues'
/// test project cannot reference nine section assemblies to read the real seams, so it restates
/// them; each owning section pins its own declaration in its own test project, and this stays a
/// fixture for the lookup rather than a second routing table.
/// </summary>
internal static class TestIssueQueues
{
    public static IssueSectionRouting Routing(params IIssueQueueOwner[] owners) => new(owners);

    public static IssueSectionRouting Shipped() => Routing(
        new TestQueueOwner("Budget", [RoleNames.FinanceAdmin]),
        new TestQueueOwner("Camps", [RoleNames.CampAdmin]),
        new TestQueueOwner("CityPlanning", [RoleNames.CampAdmin]),
        new TestQueueOwner("Governance", [RoleNames.Board]),
        new TestQueueOwner("Onboarding",
            [RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator, RoleNames.HumanAdmin]),
        new TestQueueOwner("Scanner", [RoleNames.TicketAdmin, RoleNames.Board]),
        new TestQueueOwner("Shifts", [RoleNames.NoInfoAdmin]),
        new TestQueueOwner("Teams", [RoleNames.TeamsAdmin]),
        new TestQueueOwner("Tickets", [RoleNames.TicketAdmin]));
}
