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
        foreach (var row in AccessMatrixDefinitions.Rows)
        {
            sb.AppendLine(FormattableString.Invariant(
                $"- **{row.Feature}** — {string.Join(", ", row.AllowedRoles)}"));
        }
        return sb.ToString();
    }

    public string BuildGlossariesMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Section Glossaries");
        foreach (var (section, body) in SectionHelpContent.AllGlossaries())
        {
            sb.AppendLine();
            sb.AppendLine(FormattableString.Invariant($"## {section}"));
            sb.AppendLine(body);
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
}
