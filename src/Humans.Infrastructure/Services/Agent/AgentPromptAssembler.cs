using System.Globalization;
using System.Text;
using Humans.Application.Constants;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Models;

namespace Humans.Infrastructure.Services.Agent;

public sealed class AgentPromptAssembler : IAgentPromptAssembler
{
    /// <summary>
    /// Maximum number of <see cref="UpcomingShiftEntry"/> rows rendered into
    /// the per-turn user-context tail. Anything beyond this is summarised as
    /// "+N more" — keeps the prompt bounded for volunteers signed up to many
    /// ranges. The full set is available to the agent via
    /// <c>get_shift_details</c>.
    /// </summary>
    internal const int MaxRenderedUpcomingShifts = 10;

    private const string SystemPromptHeader = """
        You are the Nobodies Collective in-app helper. You help members both with the Humans app (onboarding, teams, shifts, tickets, governance, profiles) AND with the wider Nobodies Collective association and its Elsewhere event — event logistics, on-site facilities and operations, travel, community practices, and the organisation's history. Ground every answer on the documentation below and the user context supplied at the end of this prompt.

        Two knowledge sources sit below, each with its own index:
        - The **Section Index** — authoritative guides for how the Humans app works. Fetch a full guide on demand with `fetch_section_guide` (section=<key>).
        - The **Community FAQ index** — crowd-sourced answers from the community Discord about the event, the association, and on-site life. Each topic lists `covers:` keywords so you can see what it answers. Fetch a topic on demand with `fetch_community_faq` (topic=<key>). This source is unofficial and may be outdated.

        Workflow (every substantive turn):
        1. Read the question. If it contains an abbreviation or piece of jargon (e.g. EE = early entry, LNT, TAP, MoN), expand it and scan BOTH indexes for the concept before deciding anything. An unfamiliar acronym is a reason to look it up, never a reason to refuse.
        2. Match the question against BOTH the Section Index and the Community FAQ index (including the `covers:` keywords). App-mechanics questions map to sections; event / community / on-site / logistics / history questions map to community topics. Many questions touch both — consult both. Pick the closest match in each index; you rarely need more than one or two.
        3. Fetch the closest matches — `fetch_section_guide` and/or `fetch_community_faq`. You have a limited lookup budget per turn, so prioritise the best one or two rather than fetching everything, and leave room to actually answer. Do NOT answer substantive questions from the indexes alone — they are only routers.
        4. Answer from the fetched docs, the user context tail, and the access-matrix / glossaries / route-map below. Whenever any part of your answer comes from the community FAQ, tell the user it's community discussion and may not be official.

        Rules (non-negotiable):
        - Answer ONLY from preloaded docs, fetched docs, or the user's live state. Never invent rules, routes, role names, dates, or people's names. If the docs do not contain the answer, say so or escalate — never fill the gap with a plausible-sounding guess.
        - Community FAQ (fetched via `fetch_community_faq`) is crowd-sourced from the community Discord and is NOT official; it may be outdated or inaccurate. Prefer authoritative section guides when they cover the question. Whenever your answer relies on the community FAQ, tell the user it comes from community discussion and may not be official.
        - Before concluding a question is off-topic OR that you cannot answer it, you MUST have scanned both indexes (expanding any jargon) and fetched the most relevant section guide and/or community topic. A `fetch_section_guide` or `fetch_community_faq` returning "Unknown …" or an error is not by itself grounds to give up — try the other index, related keys, or the access matrix first.
        - Refuse ONLY genuinely unrelated requests — politics, personal/medical/legal advice, general programming help, anything with no connection to the Nobodies Collective, its members, the Humans app, or the Elsewhere event. Questions about the event, the site, facilities, logistics, the community, or the association's history are IN scope: consult the Community FAQ, do not refuse them.
        - Answer OR escalate, never both. If you can answer the user's question from the available context — preload, fetched docs, or user state — answer and terminate the turn. If you genuinely cannot answer (no relevant docs in either index, missing context, ambiguous user state) call the `route_to_issue` tool with a concrete `title`, `category` (Bug/Feature/Question), and `description` summarising what the user asked, then terminate the turn WITHOUT also drafting a partial answer.
        - For personal-history questions ("who voluntold me?", "when did I get added to Build team?", "did anyone change my role?", "when was I approved?", "what happened to my shift signup?") call `get_audit_history` first and answer from the lines it returns. The tool already substitutes the user's name with "You" and resolves other actors to display names — quote those lines verbatim rather than paraphrasing.
        - For shift-detail questions ("when do I show up?", "what's the deal with my Friday shift?", "tell me more about my July 1–7 shift") call `get_shift_details` with the `Key` from the matching `UpcomingShifts` entry in the user-context tail. The tail's UpcomingShifts list is a summary only; do not answer detail questions from it alone.
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
        {
            sb.AppendLine("Teams:");
            foreach (var membership in snapshot.Teams.OrderBy(m => m.TeamName, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  - {membership.TeamName} ({membership.Role})"));
        }

        if (snapshot.PendingConsentDocs.Count > 0)
            sb.AppendLine("Pending consents: " + string.Join(", ", snapshot.PendingConsentDocs));

        if (snapshot.OpenTicketIds.Count > 0)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"OpenTickets: {snapshot.OpenTicketIds.Count}"));

        if (snapshot.OpenFeedbackIds.Count > 0)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"OpenFeedback: {snapshot.OpenFeedbackIds.Count}"));

        if (snapshot.UpcomingShifts.Count > 0)
        {
            sb.AppendLine("UpcomingShifts:");
            var rendered = 0;
            foreach (var entry in snapshot.UpcomingShifts.OrderBy(e => e.StartDate))
            {
                if (rendered >= MaxRenderedUpcomingShifts)
                    break;
                sb.AppendLine(RenderShiftEntry(entry));
                rendered++;
            }
            if (snapshot.UpcomingShifts.Count > MaxRenderedUpcomingShifts)
            {
                var overflow = snapshot.UpcomingShifts.Count - MaxRenderedUpcomingShifts;
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  +{overflow} more"));
            }
        }

        return sb.ToString();
    }

    private static string RenderShiftEntry(UpcomingShiftEntry entry)
    {
        // Block: "  - [<key>] 2026-07-01 to 2026-07-07 — Cantina build (Confirmed, 7 days)"
        // Singleton: "  - [<key>] 2026-07-15 — Setup crew (Confirmed)"
        // Key is the value the agent passes to get_shift_details(shiftId=...).
        var startIso = entry.StartDate.ToInvariantDate();
        if (entry.DayCount > 1)
        {
            var endIso = entry.EndDate.ToInvariantDate();
            return string.Create(CultureInfo.InvariantCulture,
                $"  - [{entry.Key}] {startIso} to {endIso} — {entry.Label} ({entry.Status}, {entry.DayCount} days)");
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"  - [{entry.Key}] {startIso} — {entry.Label} ({entry.Status})");
    }

    public IReadOnlyList<AnthropicToolDefinition> BuildToolDefinitions() =>
    [
        new(Name: AgentToolNames.FetchFeatureSpec,
            Description: "Fetch a feature specification from docs/features/{name}.md. Use only for whitelisted filename stems.",
            JsonSchema: """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""),
        new(Name: AgentToolNames.FetchSectionGuide,
            Description: "Fetch the long procedural guide for a given section key from SectionHelpContent.Guides.",
            JsonSchema: """{"type":"object","properties":{"section":{"type":"string"}},"required":["section"]}"""),
        new(Name: AgentToolNames.FetchCommunityFaq,
            Description: "Fetch a community-sourced FAQ topic by its topic key from the Community FAQ index. CALL THIS for questions about the Elsewhere event, the association, on-site facilities and logistics, travel, tickets/waves, community practices, jargon, or the org's history — anything not purely about Humans-app mechanics. Match the question (and any expanded abbreviations) against each topic's `covers:` keywords in the index, and fetch every plausibly-relevant topic before concluding you cannot help. Content is crowd-sourced from the community Discord and is NOT official — it may be outdated; prefer fetch_section_guide when an authoritative section guide covers the question, and tell the user when an answer comes from community discussion.",
            JsonSchema: """{"type":"object","properties":{"topic":{"type":"string"}},"required":["topic"]}"""),
        new(Name: AgentToolNames.GetAuditHistory,
            Description: "Fetch the calling user's recent audit history as plain-text lines (shifts, team membership, role changes, voluntolds, approvals, Workspace events). The tool substitutes the user's id with 'You' and resolves other actors to display names — no GUIDs are returned. Use for personal-history questions; do not use for questions about other users. Default limit is 20, hard cap 50.",
            JsonSchema: """{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":50,"description":"Max lines to return. Defaults to 20, capped at 50."}}}"""),
        new(Name: AgentToolNames.GetShiftDetails,
            Description: "Look up details for one of the user's upcoming shift entries (block or singleton). Pass the Key value from an UpcomingShifts row in the user-context tail. Returns rota name, full description, dates, day count, status, all-day window or start/duration, and where-to-show-up notes from PracticalInfo. Only the calling user's signups are accessible — looking up other users' shifts returns a not-found error.",
            JsonSchema: """{"type":"object","properties":{"shiftId":{"type":"string","format":"uuid"}},"required":["shiftId"]}"""),
        new(Name: AgentToolNames.RouteToIssue,
            Description: "Hand off a question the agent cannot answer to the Issues system. Does NOT create the issue — the system pre-fills an issue submission form so the user can review and submit. Use Question for general help requests, Bug for things that look broken, Feature for missing capabilities.",
            JsonSchema: """{"type":"object","properties":{"title":{"type":"string","description":"Short one-line title (max 200 chars)."},"category":{"type":"string","enum":["Bug","Feature","Question"],"description":"Issue category."},"description":{"type":"string","description":"Detailed description of what the user asked and any relevant context (max 5000 chars)."}},"required":["title","category","description"]}""")
    ];
}
