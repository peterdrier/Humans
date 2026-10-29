using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Camps.Contracts;
using Humans.Tickets.Contracts;
using Humans.Shifts.Contracts;
using Humans.Finance.Contracts;
using Humans.Consent.Contracts;
using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;

namespace Humans.Issues.Tests.Domain;

/// <summary>A section's queue declaration, stood up by hand.</summary>
internal sealed record TestQueueOwner(string QueueKey, IReadOnlyList<string> OwningRoles) : IIssueQueueOwner
{
    public string AreaLabelResourceKey => $"Issues_Area_{QueueKey}";
    public IReadOnlyList<string> PathSegments => [];
}

/// <summary>
/// The contributions the shipped sections declare through <see cref="IIssueQueueOwner"/>. Issues'
/// test project cannot reference eleven section assemblies to read the real seams, so it restates
/// them; the real declarations are pinned against DI in
/// <c>tests/Humans.Web.Tests/Sections/IssueQueueOwnerTests.cs</c>, and this stays a fixture for
/// the lookup rather than a second routing table.
/// </summary>
internal static class TestIssueQueues
{
    public static IssueSectionRouting Routing(params IIssueQueueOwner[] owners) => new(owners);

    public static IssueSectionRouting Shipped() => Routing(
        new TestQueueOwner("Budget", [FinanceRoles.FinanceAdmin]),
        new TestQueueOwner("Camps", [CampsRoles.CampsAdmin]),
        new TestQueueOwner("CityPlanning", [CampsRoles.CampsAdmin]),
        new TestQueueOwner("Governance", [RoleNames.Board]),
        // Users declares "Profiles" and Consent declares "Legal" — the spellings their rows
        // were stored under before those sections were renamed.
        new TestQueueOwner("Legal", [ConsentRoles.ConsentCoordinator]),
        new TestQueueOwner("Onboarding",
            [ConsentRoles.ConsentCoordinator, ShiftsRoles.ShiftsAdmin, UsersRoles.UsersAdmin]),
        new TestQueueOwner("Profiles", [UsersRoles.UsersAdmin]),
        new TestQueueOwner("Scanner", [TicketsRoles.TicketsAdmin, RoleNames.Board]),
        new TestQueueOwner("Shifts", [ShiftsRoles.ShiftsNoInfo]),
        new TestQueueOwner("Teams", [TeamsRoles.TeamsAdmin]),
        new TestQueueOwner("Tickets", [TicketsRoles.TicketsAdmin]));
}
