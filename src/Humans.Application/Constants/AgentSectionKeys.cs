using System.Diagnostics.CodeAnalysis;

namespace Humans.Application.Constants;

/// <summary>
/// The section keys the agent's <c>fetch_section_guide</c> tool accepts, and the aliases that
/// resolve onto them. Pure data with no I/O, so both the Infrastructure reader that fetches
/// <c>docs/sections/{key}.md</c> and the Web layer that renders the preload corpus can depend on
/// it inward (design-rules.md §1) instead of on each other.
/// </summary>
public static class AgentSectionKeys
{
    // Every user-facing section. A section left off this list is unreachable: the agent
    // either refuses the question outright or answers it from the community Discord FAQ
    // with an "unofficial, may be outdated" disclaimer — even for a first-party feature.
    // Operator/internal-only sections (Finance, Holded, Email, Mailer, AuditLog, Debug,
    // admin-shell) stay off deliberately; add one when triage shows users asking about it.
    private static readonly HashSet<string> Canonical =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Onboarding", "Teams", "LegalAndConsent", "Governance", "Shifts",
            "Tickets", "Profiles", "Auth", "Budget", "Camps",
            "CityPlanning", "Campaigns", "Feedback", "GoogleIntegration",
            "Events", "Guide", "Store", "Scanner", "Gate",
            "Calendar", "Cantina", "Containers", "Issues"
        };

    // Names the model reads out of its own preload corpus (help-widget glossary keys and
    // access-matrix display names) and out of user jargon, mapped onto the section file they
    // are actually about. These are a different namespace from the canonical keys: the model
    // names the right section in prose and still dead-ends on the tool call. 20 such calls
    // across 9 production conversations, three of which ended in an empty reply to the user
    // (nobodies-collective/Humans#949). Every value must be a canonical key.
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Profile"] = "Profiles",
            ["OnboardingReview"] = "Onboarding",
            // /Admin is a nav holder, not a section; its help glossary is governance/ops terms.
            ["Admin"] = "Governance",
            ["Board"] = "Governance",
            // A barrio is a camp — Camps.md defines them; CityPlanning.md only places them.
            ["Barrios"] = "Camps",
            ["CityPlanningOverview"] = "CityPlanning",
            ["CityPlanningBarrioMap"] = "CityPlanning",
            ["ContainerMap"] = "Containers",
        };

    /// <summary>Every key a section guide can be fetched under, in canonical casing.</summary>
    public static IReadOnlySet<string> All => Canonical;

    /// <summary>
    /// Resolves a caller-supplied section key — canonical, any casing, or a known alias — to the
    /// canonical key whose <c>docs/sections/{key}.md</c> file backs it. Casing matters because
    /// GitHub paths are case-sensitive and LLMs routinely lowercase the key (e.g. "shifts"), so
    /// the fetched filename must be the canonical one ("Shifts.md").
    /// </summary>
    public static bool TryResolve(string key, [NotNullWhen(true)] out string? canonicalKey) =>
        Canonical.TryGetValue(key, out canonicalKey) || Aliases.TryGetValue(key, out canonicalKey);
}
