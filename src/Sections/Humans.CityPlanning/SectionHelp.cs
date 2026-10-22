using Humans.Base.Interfaces;
using Humans.Base.Models;

namespace Humans.CityPlanning;

/// <summary>The markdown lives in <c>Docs/help/</c> and is embedded into this assembly.</summary>
internal sealed class SectionHelp : ISectionHelp
{
    public IReadOnlyList<SectionHelpEntry> HelpEntries => Entries;

    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(SectionHelp).Assembly, "ContainerMap", 100),
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(SectionHelp).Assembly, "CityPlanningOverview", 110),
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(SectionHelp).Assembly, "CityPlanningBarrioMap", 120),
    ];
}
