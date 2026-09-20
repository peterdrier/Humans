using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Issues.Contracts;

namespace Humans.Tickets.Tests.Architecture;

/// <summary>
/// Tickets declares the issue queue it owns. Issues routes on what DI discovered, so this
/// declaration is the whole routing entry — pinned here, where the section that owns it lives.
/// </summary>
public class IssueQueueOwnerTests
{
    private static readonly IIssueQueueOwner Owner = new Section();

    [HumansFact]
    public void Section_declares_the_issue_queue_it_owns()
    {
        Owner.QueueKey.Should().Be("Tickets");
        Owner.OwningRoles.Should().Equal(RoleNames.TicketAdmin);
    }

    [HumansFact]
    public void Queue_declaration_publishes_its_own_section_annotation()
    {
        var annotation = Owner.Annotations().Should().ContainSingle().Subject;

        annotation.Section.Should().Be("Tickets");
        annotation.Facet.Should().Be("Issue queue");
    }
}
