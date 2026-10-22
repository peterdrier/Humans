using System.Diagnostics.CodeAnalysis;

namespace Humans.Agent.Contracts;

/// <summary>
/// Pure data, so the guide reader and the Web preload renderer can both depend on it instead of on each other.
/// </summary>
public static class AgentSectionKeys
{
    // A section left off this list is unreachable: the agent refuses or answers from the community FAQ with an
    // "unofficial" disclaimer, even for a first-party feature. Operator-only sections stay off deliberately; add one
    // when triage shows users asking about it.
    private static readonly HashSet<string> Canonical =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Onboarding", "Teams", "Consent", "Governance", "Shifts",
            "Tickets", "Users", "Auth", "Budget", "Camps",
            "CityPlanning", "Campaigns", "Feedback", "GoogleIntegration",
            "Events", "Guide", "Store", "Scanner", "Gate",
            "Calendar", "Cantina", "Containers", "Issues"
        };

    // Names the model meets in its own preload corpus and in user jargon, mapped onto the section they are about.
    // Without them it names the right section in prose and still dead-ends the tool call. Every value must be a
    // canonical key.
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // The canonical key follows the project name because the reader probes
            // src/Sections/Humans.{key}/Docs — there is no Humans.Profiles project —
            // but users and the model still say "profile".
            ["Profiles"] = "Users",
            ["Profile"] = "Users",
            ["OnboardingReview"] = "Onboarding",
            // Kept as an alias because the model still meets the old name in its own
            // context: docs/guide/ has not been renamed (GuideFiles.Sections still lists
            // "LegalAndConsent"), and stored conversations emit it.
            ["LegalAndConsent"] = "Consent",
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
    /// Accepts a canonical key in any casing, or an alias. Casing matters: GitHub paths are case-sensitive and models
    /// routinely lowercase the key.
    /// </summary>
    public static bool TryResolve(string key, [NotNullWhen(true)] out string? canonicalKey) =>
        Canonical.TryGetValue(key, out canonicalKey) || Aliases.TryGetValue(key, out canonicalKey);
}
