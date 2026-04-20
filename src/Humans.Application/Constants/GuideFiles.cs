namespace Humans.Application.Constants;

/// <summary>
/// The set of markdown files rendered at /Guide/{stem}. Order mirrors docs/guide/README.md.
/// </summary>
public static class GuideFiles
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
        "Email",
        "Campaigns",
        "Feedback",
        "Governance",
        "Budget",
        "CityPlanning",
        "GoogleIntegration",
        "Admin"
    ];

    public static readonly IReadOnlySet<string> All = BuildAll();

    private static IReadOnlySet<string> BuildAll()
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
        return set;
    }
}
