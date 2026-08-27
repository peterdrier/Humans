using System.Reflection;
using AwesomeAssertions;
using Humans.Agent.Contracts;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Sections;

/// <summary>
/// The section list published from DI at startup (nobodies-collective/Humans#1509): that it
/// describes what the app actually composes, and that a contributed fact naming no section
/// surfaces instead of vanishing — which is the whole reason the catalog exists.
/// </summary>
public class SectionCatalogTests
{
    private sealed class Annotator(params SectionAnnotation[] annotations) : ISectionAnnotations
    {
        public IEnumerable<SectionAnnotation> Annotations() => annotations;
    }

    private static IReadOnlyList<(string Name, Assembly Assembly, ISection Section)> Shipped() =>
        SectionDiscoveryExtensions.ShippedSections();

    private static ISectionCatalog Build(params ISectionAnnotations[] annotators) =>
        SectionCatalogBuilder.Build(Shipped(), annotators);

    [HumansFact]
    public void Catalog_Describes_Every_Shipped_Section()
    {
        var catalog = Build();

        catalog.Sections.Should().HaveCount(Shipped().Count);
        catalog.Sections.Select(s => s.Name).Should().Contain(["Issues", "Guide", "Agent", "Debug"]);
    }

    [HumansFact]
    public void A_Table_Owning_Section_Reports_Its_Context()
    {
        Build().Sections.Single(s => string.Equals(s.Name, "Issues", StringComparison.Ordinal))
            .DbContexts.Should().Contain("IssuesDbContext");
    }

    [HumansFact]
    public void Section_Facts_Are_Derived_From_The_Assembly_Not_Declared()
    {
        var issues = Build().Sections.Single(s => string.Equals(s.Name, "Issues", StringComparison.Ordinal));

        issues.HasContracts.Should().BeTrue();
        issues.Seams.Should().Contain("ISectionAnnotations");
        issues.ServiceInterfaces.Should().Contain("IIssuesService");
        issues.Repositories.Should().Contain("IIssuesRepository");
        issues.DependsOn.Should().Contain("Users");
    }

    [HumansFact]
    public void An_Annotation_Lands_On_Its_Section_Whatever_Its_Casing()
    {
        var catalog = Build(new Annotator(new SectionAnnotation("iSsUeS", "Guide page", "/Guide/Issues")));

        catalog.Sections.Single(s => string.Equals(s.Name, "Issues", StringComparison.Ordinal)).Annotations
            .Should().ContainSingle(a => string.Equals(a.Facet, "Guide page", StringComparison.Ordinal));
        catalog.UnmatchedAnnotations.Should().BeEmpty();
    }

    [HumansFact]
    public void An_Annotation_Naming_No_Section_Is_Surfaced_Not_Dropped()
    {
        var catalog = Build(new Annotator(
            new SectionAnnotation("Profiles", "Issue queue", "HumanAdmin"),
            new SectionAnnotation("Issues", "Issue queue", "Admin")));

        catalog.UnmatchedAnnotations.Should().ContainSingle()
            .Which.Section.Should().Be("Profiles");
        catalog.Sections.SelectMany(s => s.Annotations).Should().ContainSingle();
    }

    [HumansFact]
    public void TryResolve_Canonicalizes_Casing_And_Rejects_What_Is_Not_A_Section()
    {
        var catalog = Build();

        catalog.TryResolve("issues", out var canonical).Should().BeTrue();
        canonical.Should().Be("Issues");

        catalog.TryResolve("  Issues  ", out _).Should().BeTrue();
        catalog.TryResolve("NotASection", out _).Should().BeFalse();
        catalog.TryResolve(null, out _).Should().BeFalse();
        catalog.TryResolve("   ", out _).Should().BeFalse();
    }

    [HumansFact]
    public void Every_Section_The_Agent_Can_Fetch_A_Doc_For_Is_A_Real_Section()
    {
        // The agent's canonical keys are a deliberate subset of the sections — operator-only
        // ones are off it — but every key on it must still name a section that exists, or the
        // tool dead-ends and the model answers from the community FAQ instead.
        var catalog = Build();

        AgentSectionKeys.All.Where(key => !catalog.TryResolve(key, out _))
            .Should().BeEmpty("every agent doc key must name a discovered section");
    }

    [HumansFact]
    public void The_Real_Contributions_Are_Discovered()
    {
        var annotators = SectionDiscoveryExtensions.DiscoverImplementations<ISectionAnnotations>();

        annotators.Select(a => a.GetType().Assembly.GetName().Name)
            .Should().Contain(["Humans.Agent", "Humans.Guide", "Humans.Issues"]);

        SectionCatalogBuilder.Build(Shipped(), annotators)
            .Sections.SelectMany(s => s.Annotations)
            .Select(a => a.Facet).Distinct(StringComparer.Ordinal)
            .Should().Contain(["Agent doc key", "Guide page", "Issue queue"]);
    }
}
