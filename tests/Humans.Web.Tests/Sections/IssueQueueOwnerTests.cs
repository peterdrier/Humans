using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;
using Humans.Issues.Models;
using Humans.Web.Extensions;
using Xunit;

namespace Humans.Web.Tests.Sections;

/// <summary>
/// Issues routes on what DI discovered, so the declarations here are the whole routing table: a section joining or
/// leaving a queue edits <see cref="DeclaredQueues"/>, never Issues.
/// </summary>
public class IssueQueueOwnerTests
{
    private static IReadOnlyList<(string Section, IIssueQueueOwner Owner)> Owners() =>
        [.. SectionDiscoveryExtensions.ShippedSections()
            .Where(s => s.Section is IIssueQueueOwner)
            .Select(s => (s.Name, Owner: (IIssueQueueOwner)s.Section))
            .OrderBy(o => o.Name, StringComparer.Ordinal)];

    /// <summary>
    /// <c>Admin</c> handles every queue and is never declared. Users declares <c>Profiles</c> and Consent <c>Legal</c>
    /// because stored rows carry those spellings.
    /// </summary>
    private static readonly (string Section, string Key, string[] Roles)[] Declared =
    [
        ("Budget", "Budget", [RoleNames.FinanceAdmin]),
        ("Camps", "Camps", [RoleNames.CampAdmin]),
        ("CityPlanning", "CityPlanning", [RoleNames.CampAdmin]),
        ("Consent", "Legal", [RoleNames.ConsentCoordinator]),
        ("Governance", "Governance", [RoleNames.Board]),
        ("Onboarding", "Onboarding",
            [RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator, RoleNames.HumanAdmin]),
        ("Scanner", "Scanner", [RoleNames.TicketAdmin, RoleNames.Board]),
        ("Shifts", "Shifts", [RoleNames.NoInfoAdmin]),
        ("Teams", "Teams", [RoleNames.TeamsAdmin]),
        ("Tickets", "Tickets", [RoleNames.TicketAdmin]),
        ("Users", "Profiles", [RoleNames.HumanAdmin]),
    ];

    public static TheoryData<string, string, string[]> DeclaredQueues
    {
        get
        {
            var data = new TheoryData<string, string, string[]>();
            foreach (var (section, key, roles) in Declared) data.Add(section, key, roles);
            return data;
        }
    }

    [HumansTheory]
    [MemberData(nameof(DeclaredQueues))]
    public void Section_declares_the_issue_queue_it_owns(string section, string key, string[] roles)
    {
        var owner = Owners().Single(o => string.Equals(o.Section, section, StringComparison.Ordinal)).Owner;

        owner.QueueKey.Should().Be(key);
        owner.OwningRoles.Should().Equal(roles);
    }

    [HumansFact]
    public void The_sections_declaring_a_queue_are_exactly_the_pinned_set()
    {
        Owners().Select(o => o.Section).Should()
            .Equal([.. Declared.Select(d => d.Section).OrderBy(s => s, StringComparer.Ordinal)]);
    }

    [HumansFact]
    public void Every_declaration_is_usable_as_a_routing_entry()
    {
        foreach (var (section, owner) in Owners())
        {
            owner.QueueKey.Should().NotBeNullOrWhiteSpace($"{section} declares a queue key");
            owner.OwningRoles.Should().NotBeEmpty($"{section}'s queue needs a handler besides Admin");
            owner.OwningRoles.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r));
            owner.OwningRoles.Should().OnlyHaveUniqueItems();
            owner.OwningRoles.Should().NotContain(RoleNames.Admin, "Admin handles every queue implicitly");
        }
    }

    [HumansFact]
    public void Declared_paths_and_labels_are_available_without_a_hub_mapping()
    {
        var owners = Owners();
        var routing = new IssueSectionRouting(owners.Select(o => o.Owner));
        foreach (var (_, owner) in owners)
        {
            routing.AreaLabelResourceKeys[owner.QueueKey].Should().Be(owner.AreaLabelResourceKey);
            foreach (var segment in owner.PathSegments)
            {
                IssueSectionInference.FromPath($"/{segment}/item", routing).Should().Be(owner.QueueKey);
            }
        }
    }

    [HumansFact]
    public void Queue_keys_are_unique_across_sections()
    {
        Owners().Select(o => o.Owner.QueueKey).Should().OnlyHaveUniqueItems();
    }

    [HumansFact]
    public void Every_declaration_publishes_its_own_queue_annotation()
    {
        foreach (var (section, owner) in Owners())
        {
            var annotation = owner.Annotations().Should().ContainSingle($"{section} publishes one").Subject;

            annotation.Section.Should().Be(owner.QueueKey);
            annotation.Facet.Should().Be("Issue queue");
            annotation.Detail.Should().Be(string.Join(", ", owner.OwningRoles));
        }
    }
}
