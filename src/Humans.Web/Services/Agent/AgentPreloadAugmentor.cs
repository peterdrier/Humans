using System.Text;
using Humans.Agent.Contracts;
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
        // seven times in production (nobodies-collective/Humans#949). Each block is emitted under
        // the section key the tool actually accepts. Pages sharing a key are collected under one
        // heading but keep their own table and page label: several define the same term with
        // different emphasis ("Barrio Lead" three ways across the city-planning pages), so folding
        // them into one table would either duplicate the term or drop a definition.
        var glossaries = SectionHelpContent.AllGlossaries()
            .Select(g => (Section: ResolveSectionKey(g.Section), Page: PageTitle(g.Body, g.Section), Rows: TermRows(g.Body)))
            .GroupBy(g => g.Section, StringComparer.Ordinal)
            .ToList();

        // A term row that appears verbatim on more than one page is shared: emitted once up
        // front and omitted from the per-page tables.
        var sharedRows = glossaries
            .SelectMany(g => g.SelectMany(p => p.Rows))
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
        AppendTermTable(sb, glossaries.SelectMany(g => g.SelectMany(p => p.Rows))
            .Where(sharedRows.Contains).Distinct(StringComparer.Ordinal));

        foreach (var section in glossaries)
        {
            var pages = section
                .Select(p => (p.Page, Rows: p.Rows.Where(r => !sharedRows.Contains(r)).ToList()))
                .Where(p => p.Rows.Count > 0)
                .ToList();
            if (pages.Count == 0)
            {
                continue;
            }

            sb.AppendLine();
            sb.AppendLine(FormattableString.Invariant($"## {section.Key} Glossary"));
            foreach (var (page, rows) in pages)
            {
                sb.AppendLine();
                // Only worth naming the page when the key covers more than one — otherwise the
                // heading already says it.
                if (pages.Count > 1)
                {
                    sb.AppendLine(FormattableString.Invariant($"*{page}:*"));
                    sb.AppendLine();
                }
                AppendTermTable(sb, rows);
            }
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
        - /Issues — report a bug, request a feature, or ask a question
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
        AgentSectionKeys.TryResolve(glossaryKey, out var section) ? section : glossaryKey;

    private static List<string> TermRows(string body) =>
        body.Split('\n').Select(l => l.TrimEnd()).Where(IsTermRow).ToList();

    /// <summary>
    /// The help page a glossary belongs to, read off its own "## &lt;title&gt; Glossary" heading so
    /// the label survives regrouping. Falls back to the glossary key for a body without one.
    /// </summary>
    private static string PageTitle(string body, string glossaryKey)
    {
        var heading = body.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("## ", StringComparison.Ordinal));
        if (heading is null)
        {
            return glossaryKey;
        }
        var title = heading["## ".Length..].Trim();
        return title.EndsWith(" Glossary", StringComparison.Ordinal)
            ? title[..^" Glossary".Length]
            : title;
    }

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
