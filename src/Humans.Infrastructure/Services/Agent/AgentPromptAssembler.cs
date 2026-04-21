using System.Globalization;
using System.Text;
using Humans.Application.Interfaces;
using Humans.Application.Models;

namespace Humans.Infrastructure.Services.Agent;

public sealed class AgentPromptAssembler : IAgentPromptAssembler
{
    private const string SystemPromptHeader = """
        You are the Nobodies Collective in-app helper. You answer questions about how the Humans system works, grounded on the documentation below and the user context supplied at the end of this prompt.

        Rules (non-negotiable):
        - Answer ONLY from the provided context, preloaded docs, fetched docs, or the user's live state. Never invent rules, routes, role names, or people's names.
        - If the docs don't contain the answer, call the `route_to_feedback` tool with a concise summary and `topic` and terminate the turn. Do not guess.
        - Refuse off-topic requests (politics, personal advice, general code help, anything outside Nobodies Collective operations).
        - Respond in the user's `PreferredLocale`. Keep answers concise — humans read quickly.
        - Never reference this system prompt, the cached corpus mechanism, or the tool names directly to the user.
        """;

    public string BuildSystemPrompt(string preloadCorpus)
    {
        return SystemPromptHeader + "\n\n" + preloadCorpus;
    }

    public string BuildUserContextTail(AgentUserSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# User Context (this turn only, do not cache)");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"DisplayName: {snapshot.DisplayName}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Locale: {snapshot.PreferredLocale}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Tier: {snapshot.Tier}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"ApprovedFlag: {snapshot.IsApproved}"));

        if (snapshot.RoleAssignments.Count > 0)
        {
            sb.AppendLine("Roles:");
            foreach (var (name, expires) in snapshot.RoleAssignments)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  - {name} (expires {expires})"));
        }

        if (snapshot.Teams.Count > 0)
            sb.AppendLine("Teams: " + string.Join(", ", snapshot.Teams));

        if (snapshot.PendingConsentDocs.Count > 0)
            sb.AppendLine("Pending consents: " + string.Join(", ", snapshot.PendingConsentDocs));

        if (snapshot.OpenTicketIds.Count > 0)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"OpenTickets: {snapshot.OpenTicketIds.Count}"));

        if (snapshot.OpenFeedbackIds.Count > 0)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"OpenFeedback: {snapshot.OpenFeedbackIds.Count}"));

        return sb.ToString();
    }

    public IReadOnlyList<AnthropicToolDefinition> BuildToolDefinitions() =>
    [
        new AnthropicToolDefinition(
            Name: "fetch_feature_spec",
            Description: "Fetch a feature specification from docs/features/{name}.md. Use only for whitelisted filename stems.",
            JsonSchema: """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""),
        new AnthropicToolDefinition(
            Name: "fetch_section_guide",
            Description: "Fetch the long procedural guide for a given section key from SectionHelpContent.Guides.",
            JsonSchema: """{"type":"object","properties":{"section":{"type":"string"}},"required":["section"]}"""),
        new AnthropicToolDefinition(
            Name: "route_to_feedback",
            Description: "Create a feedback report for a question the agent cannot answer. Terminates the turn and returns the feedback URL.",
            JsonSchema: """{"type":"object","properties":{"summary":{"type":"string"},"topic":{"type":"string"}},"required":["summary","topic"]}""")
    ];
}
