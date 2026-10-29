using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Services;

namespace Humans.AuditLog.Tests;

public class AuditActionRegistryTests
{
    private sealed class SectionActions : AuditActionContributor
    {
        public static readonly AuditActionDefinition Plain = new("Plain", Verb: "did");

        public static readonly AuditActionDefinition Loud = new("Loud", Alert: true, QuickFilter: "Loud ones");

        public static readonly AuditActionDefinition Filtered = new("Filtered", QuickFilter: "Filtered ones");
    }

    private sealed class DuplicateActions : AuditActionContributor
    {
        public static readonly AuditActionDefinition Plain = new("Plain");
    }

    private sealed class OverLongActions : AuditActionContributor
    {
        public static readonly AuditActionDefinition TooLong = new(new string('x', AuditActionDefinition.NameMaxLength + 1));
    }

    [HumansFact]
    public void ContributorExposesItsStaticFieldsInDeclarationOrder()
    {
        new SectionActions().Actions.Should().Equal(SectionActions.Plain, SectionActions.Loud, SectionActions.Filtered);
    }

    [HumansFact]
    public void ResolveReturnsTheDeclaredDefinitionOrABareOne()
    {
        var registry = new AuditActionRegistry([new SectionActions()]);

        registry.Resolve("Plain").Should().BeSameAs(SectionActions.Plain);
        registry.Resolve("Unknown").Should().Be(new AuditActionDefinition("Unknown"));
    }

    [HumansFact]
    public void CanonicalNameIgnoresCaseAndLeavesUnknownNamesAlone()
    {
        var registry = new AuditActionRegistry([new SectionActions()]);

        registry.CanonicalName("plain").Should().Be("Plain");
        registry.CanonicalName("Nope").Should().Be("Nope");
    }

    [HumansFact]
    public void AlertsAndQuickFiltersComeFromTheFlags()
    {
        var registry = new AuditActionRegistry([new SectionActions()]);

        registry.Alerts.Should().Equal(SectionActions.Loud);
        registry.AlertNames.Should().Equal("Loud");
        registry.QuickFilters.Should().Equal(SectionActions.Loud, SectionActions.Filtered);
    }

    [HumansFact]
    public void DuplicateNameAcrossContributorsFailsConstruction()
    {
        var act = () => new AuditActionRegistry([new SectionActions(), new DuplicateActions()]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Plain*");
    }

    [HumansFact]
    public void OverLongNameFailsConstruction()
    {
        var act = () => new AuditActionRegistry([new OverLongActions()]);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{AuditActionDefinition.NameMaxLength}*");
    }

    [HumansFact]
    public void TheLegacyCatalogDeclaresOnlyEnumValues_EachOnce()
    {
        var names = new LegacyAuditActions().Actions.Select(a => a.Name).ToList();

        names.Should().BeSubsetOf(Enum.GetNames<AuditAction>());
        names.Should().OnlyHaveUniqueItems();
    }
}
