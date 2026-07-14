using System.Text;
using Humans.Application.Interfaces;
using Humans.Web.Models;

namespace Humans.Web.Services.Agent;

public sealed class AgentPreloadAugmentor : IAgentPreloadAugmentor
{
    public string BuildAccessMatrixMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Access Matrix");
        sb.AppendLine();
        sb.AppendLine("Per section: the roles allowed to use each feature. Roles not listed for a feature do not have access.");
        foreach (var section in AccessMatrixDefinitions.Sections.Values)
        {
            sb.AppendLine();
            sb.AppendLine(FormattableString.Invariant($"## {section.SectionName}"));
            var allowedByFeature = section.Features
                .Select(f => (f.Name, Roles: f.RoleAccess
                    .Where(kv => kv.Value == AccessLevel.Allowed)
                    .Select(kv => kv.Key)
                    .ToList()))
                .Where(f => f.Roles.Count > 0);
            foreach (var group in allowedByFeature.GroupBy(f => string.Join(", ", f.Roles), StringComparer.Ordinal))
            {
                sb.AppendLine(FormattableString.Invariant(
                    $"- **{group.Key}** — {string.Join("; ", group.Select(f => f.Name))}"));
            }
        }
        return sb.ToString();
    }

    public string BuildGlossariesMarkdown()
    {
        var glossaries = SectionHelpContent.AllGlossaries()
            .Select(g => (g.Section, Lines: g.Body.Split('\n').Select(l => l.TrimEnd()).ToList()))
            .ToList();

        // A term row that appears verbatim in more than one section glossary is shared:
        // emitted once up front and omitted from the per-section tables.
        var sharedRows = glossaries
            .SelectMany(g => g.Lines.Where(IsTermRow))
            .GroupBy(l => l, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine("# Section Glossaries");
        sb.AppendLine();
        sb.AppendLine("## Shared Terms");
        sb.AppendLine();
        sb.AppendLine("Terms used with the same meaning across sections — defined once here, omitted from the per-section tables.");
        sb.AppendLine();
        sb.AppendLine("| Term | Definition |");
        sb.AppendLine("|------|-----------|");
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in glossaries.SelectMany(g => g.Lines).Where(sharedRows.Contains))
        {
            if (emitted.Add(row))
            {
                sb.AppendLine(row);
            }
        }

        foreach (var (_, lines) in glossaries)
        {
            sb.AppendLine();
            foreach (var line in lines.Where(l => !sharedRows.Contains(l)))
            {
                sb.AppendLine(line);
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
        - /Feedback — submit a bug, feature request, or question
        - /Agent — conversational helper (this tool's own history page)
        """;

    public string BuildFaqMarkdown() =>
        "# Frequently Asked Questions" + Environment.NewLine + Environment.NewLine +
        "Distilled from real user questions. Prefer these answers; they are verified against the live app." +
        Environment.NewLine + Environment.NewLine + SectionHelpContent.Faq;

    /// <summary>A glossary table data row ("| **Term** | Definition |") — as opposed to headings and the table header.</summary>
    private static bool IsTermRow(string line) => line.StartsWith("| **", StringComparison.Ordinal);
}
