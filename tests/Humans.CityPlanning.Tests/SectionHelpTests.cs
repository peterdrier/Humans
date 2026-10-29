using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// Docs/help/ ships as an embedded resource: a renamed file or missing csproj entry shows as a null Guide or Glossary.
/// </summary>
public class SectionHelpTests
{
    private static SectionHelpEntry Entry(string key) =>
        ((ISectionHelp)new Section()).HelpEntries.Single(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    [HumansFact]
    public void Contributes_its_help_entries_in_corpus_order()
    {
        ((ISectionHelp)new Section()).HelpEntries
            .Select(e => (e.Key, e.Order))
            .Should().Equal(("ContainerMap", 100), ("CityPlanningOverview", 110), ("CityPlanningBarrioMap", 120));
    }

    [HumansFact]
    public void Reads_the_containermap_guide_and_glossary_out_of_the_assembly()
    {
        var entry = Entry("ContainerMap");

        entry.Guide.Should().StartWith("## Container Placement Map");
        entry.Guide.Should().EndWith("- **Everyone else** — cannot open this page; see placed containers as a layer on the City overview map instead", "the file's trailing newline is framing, not content");
        entry.Glossary.Should().StartWith("## Container Placement Glossary");
        entry.Glossary.Should().Contain("| **Container** | A 20 ft shipping container represented as a pentagon on the map (rectangle body + triangular door end). |");
    }

    [HumansFact]
    public void Reads_the_cityplanningoverview_guide_and_glossary_out_of_the_assembly()
    {
        var entry = Entry("CityPlanningOverview");

        entry.Guide.Should().StartWith("## City Planning Overview Map");
        entry.Guide.Should().EndWith("Map Admins always see both links, regardless of the placement phase.", "the file's trailing newline is framing, not content");
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
