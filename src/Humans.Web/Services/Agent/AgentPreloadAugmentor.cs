using System.Text;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Services.Preload;
using Humans.Web.Models;

namespace Humans.Web.Services.Agent;

public sealed class AgentPreloadAugmentor : IAgentPreloadAugmentor
{
    public string BuildAccessMatrixMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Access Matrix");
        sb.AppendLine();
        sb.AppendLine("Per section: the roles that can use each feature; \"(limited)\" marks partial/restricted access. Roles not listed for a feature do not have access.");
        foreach (var section in AccessMatrixDefinitions.Sections.Values)
        {
            sb.AppendLine();
            sb.AppendLine(FormattableString.Invariant($"## {section.SectionName}"));
            var rolesByFeature = section.Features
                .Select(f => (f.Name, Roles: f.RoleAccess
                    .Where(kv => kv.Value != AccessLevel.Denied)
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Value == AccessLevel.Limited ? kv.Key + " (limited)" : kv.Key)
                    .ToList()))
                .Where(f => f.Roles.Count > 0);
            foreach (var group in rolesByFeature.GroupBy(f => string.Join(", ", f.Roles), StringComparer.Ordinal))
            {
                sb.AppendLine(FormattableString.Invariant(
                    $"- **{group.Key}** — {string.Join("; ", group.Select(f => f.Name))}"));
            }
        }
        return sb.ToString();
    }

    public string BuildGlossariesMarkdown()
    {
        // Glossary keys name help-widget pages, not docs/sections files. The model used to read
        // "## Profile Glossary" out of this block and dead-end on fetch_section_guide("Profile") —
        // seven times in production (nobodies-collective/Humans#949). Emit each block under the
        // section key the tool actually accepts, merging blocks that resolve to the same guide.
        var glossaries = SectionHelpContent.AllGlossaries()
            .GroupBy(g => ResolveSectionKey(g.Section), StringComparer.Ordinal)
            .Select(g => (Section: g.Key, Rows: g
                .SelectMany(x => x.Body.Split('\n').Select(l => l.TrimEnd()).Where(IsTermRow))
                .Distinct(StringComparer.Ordinal)
                .ToList()))
            .ToList();

        // A term row that appears verbatim under more than one section key is shared:
        // emitted once up front and omitted from the per-section tables.
        var sharedRows = glossaries
            .SelectMany(g => g.Rows)
            .GroupBy(l => l, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine("# Section Glossaries");
        sb.AppendLine();
        sb.AppendLine("Each \"## <key> Glossary\" heading below is a section key you can pass to `fetch_section_guide`.");
        sb.AppendLine();
        sb.AppendLine("## Shared Terms");
        sb.AppendLine();
        sb.AppendLine("Terms used with the same meaning across sections — defined once here, omitted from the per-section tables.");
        sb.AppendLine();
        AppendTermTable(sb, glossaries.SelectMany(g => g.Rows).Where(sharedRows.Contains).Distinct(StringComparer.Ordinal));

        foreach (var (section, rows) in glossaries)
        {
            var own = rows.Where(r => !sharedRows.Contains(r)).ToList();
            if (own.Count == 0)
            {
                continue;
            }
            sb.AppendLine();
            sb.AppendLine(FormattableString.Invariant($"## {section} Glossary"));
            sb.AppendLine();
            AppendTermTable(sb, own);
        }
        return sb.ToString();
    }

    public string BuildRouteMapMarkdown() =>
        """
        # Route Map

        Common user-facing routes:
        - /Profile/Me — your profile
        - /Profile/Me/Emails — manage linked emails
        - /Profile/Me/Privacy — delete account / download data (GDPR)
        - /Team — team directory and join requests
        - /Shifts — shift dashboard (if you have signup access)
        - /Legal — required legal documents + consent status
        - /Feedback — submit a bug, feature request, or question
        - /Agent — conversational helper (this tool's own history page)
        """;

    public string BuildFaqMarkdown() =>
        "# Frequently Asked Questions" + Environment.NewLine + Environment.NewLine +
        "Distilled from real user questions. Prefer these answers; they are verified against the live app." +
        Environment.NewLine + Environment.NewLine + SectionHelpContent.Faq;

    /// <summary>A glossary table data row ("| **Term** | Definition |") — as opposed to headings and the table header.</summary>
    private static bool IsTermRow(string line) => line.StartsWith("| **", StringComparison.Ordinal);

    /// <summary>
    /// Maps a help-widget glossary key onto the <c>fetch_section_guide</c> key covering it. Falls
    /// back to the glossary key when nothing resolves — <c>AgentPreloadAugmentorTests</c> fails the
    /// build in that case rather than letting an unfetchable heading ship into the prompt.
    /// </summary>
    private static string ResolveSectionKey(string glossaryKey) =>
        AgentSectionDocReader.TryResolveKey(glossaryKey, out var section) ? section : glossaryKey;

    private static void AppendTermTable(StringBuilder sb, IEnumerable<string> rows)
    {
        sb.AppendLine("| Term | Definition |");
        sb.AppendLine("|------|-----------|");
        foreach (var row in rows)
        {
            sb.AppendLine(row);
        }
    }
}
