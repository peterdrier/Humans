using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Audit.Services;

namespace Humans.Audit.Tests;

public class AuditActionRegistryTests
{
    private sealed class SectionActions : AuditActionContributor
    {
        public static readonly AuditActionDefinition Plain = new("Plain", AuditKept.Forever, Verb: "did");

        public static readonly AuditActionDefinition Loud = new("Loud", AuditKept.Forever, Alert: true, QuickFilter: "Loud ones");

        public static readonly AuditActionDefinition Filtered = new("Filtered", AuditKept.Forever, QuickFilter: "Filtered ones");
    }

    private sealed class DuplicateActions : AuditActionContributor
    {
        public static readonly AuditActionDefinition Plain = new("Plain", AuditKept.Forever);
    }

    private sealed class CaseOnlyDuplicateActions : AuditActionContributor
    {
        public static readonly AuditActionDefinition Plain = new("plain", AuditKept.Forever);
    }

    private sealed class OverLongActions : AuditActionContributor
    {
        public static readonly AuditActionDefinition TooLong = new(new string('x', AuditActionDefinition.NameMaxLength + 1), AuditKept.Forever);
    }

    [HumansFact]
    public void Contributor_ExposesFieldsInDeclarationOrder()
    {
        new SectionActions().Actions.Should().Equal(SectionActions.Plain, SectionActions.Loud, SectionActions.Filtered);
    }

    [HumansFact]
    public void Resolve_UnknownName_ReturnsBareDefinition()
    {
        var registry = new AuditActionRegistry([new SectionActions()]);

        registry.Resolve("Plain").Should().BeSameAs(SectionActions.Plain);
        registry.Resolve("Unknown").Should().Be(new AuditActionDefinition("Unknown", AuditKept.Forever));
    }

    [HumansFact]
    public void CanonicalName_DifferentCase_ReturnsDeclaredName()
    {
        var registry = new AuditActionRegistry([new SectionActions()]);

        registry.CanonicalName("plain").Should().Be("Plain");
        registry.CanonicalName("Nope").Should().Be("Nope");
    }

    [HumansFact]
    public void Registry_Alerts_ComeFromFlags()
    {
        var registry = new AuditActionRegistry([new SectionActions()]);

        registry.Alerts.Should().Equal(SectionActions.Loud);
        registry.AlertNames.Should().Equal("Loud");
        registry.QuickFilters.Should().Equal(SectionActions.Loud, SectionActions.Filtered);
    }

    [HumansFact]
    public void Registry_DuplicateName_FailsConstruction()
    {
        var act = () => new AuditActionRegistry([new SectionActions(), new DuplicateActions()]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Plain*");
    }

    [HumansFact]
    public void Registry_NamesDifferByCase_FailsConstruction()
    {
        var act = () => new AuditActionRegistry([new SectionActions(), new CaseOnlyDuplicateActions()]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Plain*");
    }

    [HumansFact]
    public void Registry_OverLongName_FailsConstruction()
    {
        var act = () => new AuditActionRegistry([new OverLongActions()]);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{AuditActionDefinition.NameMaxLength}*");
    }

}
