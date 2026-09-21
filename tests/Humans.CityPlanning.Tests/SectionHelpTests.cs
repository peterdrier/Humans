using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// The help widget and the agent's glossary corpus read this section's help content through
/// the <c>ISectionHelp</c> seam, so what it contributes is pinned here rather than in Base.
/// The markdown itself lives in <c>Docs/help/</c> and ships as an embedded resource — a
/// renamed file or a missing csproj entry surfaces as a null Guide or Glossary below.
/// </summary>
public class SectionHelpTests
{
    private static SectionHelpEntry Entry(string key) =>
        new SectionHelp().HelpEntries.Single(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    [HumansFact]
    public void Contributes_its_help_entries_in_corpus_order()
    {
        new SectionHelp().HelpEntries
            .Select(e => (e.Key, e.Order))
            .Should().Equal(("ContainerMap", 100), ("CityPlanningOverview", 110), ("CityPlanningBarrioMap", 120));
    }

    [HumansFact]
    public void Reads_the_containermap_guide_and_glossary_out_of_the_assembly()
    {
        var entry = Entry("ContainerMap");

        entry.Guide.Should().StartWith("## Container Placement Map");
        entry.Guide.Should().EndWith("- **Everyone else** — read-only view of placed containers", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Container Placement Glossary");
        entry.Glossary.Should().Contain("| **Container** | A 20 ft shipping container represented as a pentagon on the map (rectangle body + triangular door end). |");
    }

    [HumansFact]
    public void Reads_the_cityplanningoverview_guide_and_glossary_out_of_the_assembly()
    {
        var entry = Entry("CityPlanningOverview");

        entry.Guide.Should().StartWith("## City Planning Overview Map");
        entry.Guide.Should().EndWith("Similarly, if container placement is open and you are a Barrio Lead, a **Go to container placement** button appears for placing your barrio's shipping containers.", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## City Planning Overview Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }

    [HumansFact]
    public void Reads_the_cityplanningbarriomap_guide_and_glossary_out_of_the_assembly()
    {
        var entry = Entry("CityPlanningBarrioMap");

        entry.Guide.Should().StartWith("## Barrio Placement Map");
        entry.Guide.Should().EndWith("- Every save broadcasts a real-time update to all connected humans via SignalR", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Barrio Placement Glossary");
        entry.Glossary.Should().Contain("| **Human** | A member of Nobodies Collective. We say \"humans\", not \"members\" or \"volunteers\". |");
    }
}
