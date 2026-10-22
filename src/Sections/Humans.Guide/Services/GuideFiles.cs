using System.Diagnostics.CodeAnalysis;

namespace Humans.Guide.Services;

/// <summary>Order mirrors docs/guide/README.md.</summary>
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
        "Issues",
        "Feedback",
        "Governance",
        "Budget",
        "Expenses",
        "Store",
        "CityPlanning",
        "GoogleIntegration",
        "Admin"
    ];

    /// <summary>The "Common questions" sidebar group; order mirrors docs/guide/README.md.</summary>
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

    /// <summary>Maps any casing to the canonical stem. False when the stem is not one of ours.</summary>
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
