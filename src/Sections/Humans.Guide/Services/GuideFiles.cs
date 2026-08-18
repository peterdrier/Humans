using System.Diagnostics.CodeAnalysis;

namespace Humans.Guide.Services;

/// <summary>
/// The set of markdown files rendered at /Guide/{stem}. Order mirrors docs/guide/README.md.
/// </summary>
internal static class GuideFiles
{
    public const string Readme = "README";
    public const string GettingStarted = "GettingStarted";
    public const string Glossary = "Glossary";

    public static readonly IReadOnlyList<string> Sections =
    [
        "Profiles",
        "Onboarding",
        "LegalAndConsent",
        "Teams",
        "Shifts",
        "Tickets",
        "Camps",
        "Events",
        "Calendar",
        "Email",
        "Campaigns",
        "Feedback",
        "Governance",
        "Budget",
        "Expenses",
        "Store",
        "CityPlanning",
        "GoogleIntegration",
        "Admin"
    ];

    /// <summary>
    /// Plain-language "Common questions" pages. Canonical home for the
    /// volunteer-facing how-to that section pages link into. Rendered as their
    /// own sidebar group; order mirrors docs/guide/README.md.
    /// </summary>
    public static readonly IReadOnlyList<string> CommonQuestions =
    [
        "EmailAccount",
        "TwoStepVerification",
        "TicketTransfers",
        "AiHelper",
        "SigningIn",
        "YourData"
    ];

    private static readonly HashSet<string> AllStems = BuildAll();

    public static IReadOnlySet<string> All => AllStems;

    /// <summary>
    /// Resolves any casing of a requested stem to the canonical spelling used in routes,
    /// cache keys and GitHub paths. False when the stem is not one of ours.
    /// </summary>
    public static bool TryCanonical(string? requested, [NotNullWhen(true)] out string? canonical)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            canonical = null;
            return false;
        }

        return AllStems.TryGetValue(requested, out canonical);
    }

    private static HashSet<string> BuildAll()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Readme,
            GettingStarted,
            Glossary
        };
        foreach (var section in Sections)
        {
            set.Add(section);
        }
        foreach (var faq in CommonQuestions)
        {
            set.Add(faq);
        }
        return set;
    }
}
