namespace Humans.Web.Models;

public enum AccessLevel
{
    Allowed,
    Limited,
    Denied
}

public class AccessMatrixData
{
    public required string SectionName { get; init; }
    public required List<string> Roles { get; init; }
    public required List<AccessMatrixFeature> Features { get; init; }
}

public class AccessMatrixFeature
{
    public required string Name { get; init; }
    public required Dictionary<string, AccessLevel> RoleAccess { get; init; }
}

public static class AccessMatrixDefinitions
{
    public static readonly Dictionary<string, AccessMatrixData> Sections = new(StringComparer.Ordinal)
    {
        ["Shifts"] = new AccessMatrixData
        {
            SectionName = "Shifts",
            Roles = ["Volunteer", "Coordinator", "NoInfoAdmin", "VolunteerCoordinator"],
            Features =
            [
                Feature("Browse shifts", "Volunteer", A, "Coordinator", A, "NoInfoAdmin", A, "VolunteerCoordinator", A),
                Feature("Sign up for shifts", "Volunteer", A, "Coordinator", A, "NoInfoAdmin", A, "VolunteerCoordinator", A),
                Feature("My Shifts & availability", "Volunteer", A, "Coordinator", A, "NoInfoAdmin", A, "VolunteerCoordinator", A),
                Feature("Create/edit rotas & shifts", "Volunteer", D, "Coordinator", A, "NoInfoAdmin", D, "VolunteerCoordinator", A),
                Feature("Approve/refuse signups", "Volunteer", D, "Coordinator", A, "NoInfoAdmin", A, "VolunteerCoordinator", A),
                Feature("Voluntell", "Volunteer", D, "Coordinator", A, "NoInfoAdmin", A, "VolunteerCoordinator", A),
                Feature("Staffing dashboard", "Volunteer", D, "Coordinator", D, "NoInfoAdmin", A, "VolunteerCoordinator", A),
            ]
        },

        ["Teams"] = new AccessMatrixData
        {
            SectionName = "Teams",
            Roles = ["Volunteer", "Coordinator", "Board", "TeamsAdmin"],
            Features =
            [
                Feature("View teams & join", "Volunteer", A, "Coordinator", A, "Board", A, "TeamsAdmin", A),
                Feature("View team details", "Volunteer", A, "Coordinator", A, "Board", A, "TeamsAdmin", A),
                Feature("Manage members", "Volunteer", D, "Coordinator", A, "Board", A, "TeamsAdmin", A),
                Feature("Manage roles", "Volunteer", D, "Coordinator", A, "Board", A, "TeamsAdmin", A),
                Feature("Create teams", "Volunteer", D, "Coordinator", D, "Board", A, "TeamsAdmin", A),
                Feature("Delete teams", "Volunteer", D, "Coordinator", D, "Board", A, "TeamsAdmin", D),
                Feature("Google resource sync", "Volunteer", D, "Coordinator", D, "Board", D, "TeamsAdmin", L),
            ]
        },

        ["Camps"] = new AccessMatrixData
        {
            SectionName = "Barrios",
            Roles = ["Volunteer", "Camp Lead", "CampAdmin"],
            Features =
            [
                Feature("Browse camps", "Volunteer", A, "Camp Lead", A, "CampAdmin", A),
                Feature("Register a camp", "Volunteer", A, "Camp Lead", A, "CampAdmin", A),
                Feature("Edit own camp", "Volunteer", D, "Camp Lead", A, "CampAdmin", A),
                Feature("Approve/reject camps", "Volunteer", D, "Camp Lead", D, "CampAdmin", A),
                Feature("Camp settings", "Volunteer", D, "Camp Lead", D, "CampAdmin", A),
            ]
        },

        ["Governance"] = new AccessMatrixData
        {
            SectionName = "Governance",
            Roles = ["Volunteer", "Board"],
            Features =
            [
                Feature("View estatutos", "Volunteer", A, "Board", A),
                Feature("Apply for tier", "Volunteer", A, "Board", A),
                Feature("View applications", "Volunteer", L, "Board", A),
                Feature("Vote on applications", "Volunteer", D, "Board", A),
            ]
        },

        ["OnboardingReview"] = new AccessMatrixData
        {
            SectionName = "Onboarding Review",
            Roles = ["ConsentCoordinator", "VolunteerCoordinator", "Board"],
            Features =
            [
                Feature("View onboarding queue", "ConsentCoordinator", A, "VolunteerCoordinator", A, "Board", A),
                Feature("Clear consent checks", "ConsentCoordinator", A, "VolunteerCoordinator", D, "Board", A),
                Feature("Flag / reject signup", "ConsentCoordinator", A, "VolunteerCoordinator", D, "Board", A),
                Feature("Board voting", "ConsentCoordinator", D, "VolunteerCoordinator", D, "Board", A),
            ]
        },

        ["Board"] = new AccessMatrixData
        {
            SectionName = "Board Dashboard",
            Roles = ["Board"],
            Features =
            [
                Feature("Dashboard & stats", "Board", A),
                Feature("Audit log", "Board", A),
                Feature("Member data export", "Board", A),
            ]
        },

        ["Tickets"] = new AccessMatrixData
        {
            SectionName = "Tickets",
            Roles = ["Board", "TicketAdmin"],
            Features =
            [
                Feature("View tickets & orders", "Board", A, "TicketAdmin", A),
                Feature("Sync operations", "Board", D, "TicketAdmin", A),
                Feature("Discount codes", "Board", A, "TicketAdmin", A),
            ]
        },

        ["Profile"] = new AccessMatrixData
        {
            SectionName = "Profile",
            Roles = ["Volunteer", "Coordinator", "Board", "HumanAdmin", "Admin"],
            Features =
            [
                Feature("View own profile", "Volunteer", A, "Coordinator", A, "Board", A, "HumanAdmin", A, "Admin", A),
                Feature("Edit own profile", "Volunteer", A, "Coordinator", A, "Board", A, "HumanAdmin", A, "Admin", A),
                Feature("View other profiles", "Volunteer", L, "Coordinator", A, "Board", A, "HumanAdmin", A, "Admin", A),
                Feature("View contact fields", "Volunteer", L, "Coordinator", L, "Board", A, "HumanAdmin", A, "Admin", A),
                Feature("Admin view of profile", "Volunteer", D, "Coordinator", D, "Board", A, "HumanAdmin", A, "Admin", A),
            ]
        },

        ["Admin"] = new AccessMatrixData
        {
            SectionName = "Admin Tools",
            Roles = ["Admin"],
            Features =
            [
                Feature("Configuration status", "Admin", A),
                Feature("Sync settings", "Admin", A),
                Feature("Email outbox", "Admin", A),
                Feature("Background jobs", "Admin", A),
                Feature("All humans list", "Admin", A),
                Feature("Role assignments", "Admin", A),
                Feature("Legal documents", "Admin", A),
            ]
        },
    };

    // Shorthand aliases for readability
    private const AccessLevel A = AccessLevel.Allowed;
    private const AccessLevel L = AccessLevel.Limited;
    private const AccessLevel D = AccessLevel.Denied;

    private static AccessMatrixFeature Feature(string name, params object[] roleAccessPairs)
    {
        var dict = new Dictionary<string, AccessLevel>(StringComparer.Ordinal);
        for (var i = 0; i < roleAccessPairs.Length; i += 2)
            dict[(string)roleAccessPairs[i]] = (AccessLevel)roleAccessPairs[i + 1];
        return new AccessMatrixFeature { Name = name, RoleAccess = dict };
    }
}
