using Humans.Domain.Constants;

namespace Humans.Web.Models;

public class StaffViewModel
{
    public IReadOnlyList<StaffRoleSectionViewModel> RoleSections { get; init; } = [];

    /// <summary>
    /// Defines the ordered list of roles with display metadata.
    /// Order: community-facing roles first, full Admin last.
    /// </summary>
    public static IReadOnlyList<StaffRoleDefinition> GetRoleDefinitions() =>
    [
        new(RoleNames.Board, "The Board",
            "Elected stewards of the collective. They guide strategy, approve tier applications, and make the big decisions so the rest of us can focus on making things happen.",
            "fa-solid fa-crown"),

        new(RoleNames.ConsentCoordinator, "Consent Coordinators",
            "Guardians of trust and safety. They review every new human's consent journey, ensuring our community standards are understood and upheld from day one.",
            "fa-solid fa-shield-heart"),

        new(RoleNames.VolunteerCoordinator, "Volunteer Coordinators",
            "The welcoming committee. They shepherd new humans through onboarding, answer the questions nobody else thinks to ask, and make sure no one gets lost along the way.",
            "fa-solid fa-hand-holding-heart"),

        new(RoleNames.TeamsAdmin, "Teams Stewards",
            "Architects of collaboration. They organize the working groups, provision resources, and make sure every team has what it needs to thrive.",
            "fa-solid fa-people-group"),

        new(RoleNames.CampAdmin, "Camp Wranglers",
            "Masters of the camp universe. They review registrations, manage settings, and keep the camp ecosystem running smoothly season after season.",
            "fa-solid fa-campground"),

        new(RoleNames.FinanceAdmin, "Finance Keepers",
            "Stewards of the purse. They manage budgets, track expenditures, and ensure every euro is accounted for with the transparency a nonprofit deserves.",
            "fa-solid fa-coins"),

        new(RoleNames.TicketAdmin, "Ticket Alchemists",
            "They conjure tickets from vendor APIs, match purchases to humans, and make sure the numbers add up before the gates open.",
            "fa-solid fa-ticket"),

        new(RoleNames.FeedbackAdmin, "Feedback Whisperers",
            "Listeners of the community voice. They triage bug reports, field feature requests, and make sure every piece of feedback finds its way to the right hands.",
            "fa-solid fa-comments"),

        new(RoleNames.HumanAdmin, "Human Administrators",
            "Keepers of the human directory. They manage profiles, handle role assignments, and provision workspace accounts. If you exist in this system, they probably helped.",
            "fa-solid fa-users-gear"),

        new(RoleNames.NoInfoAdmin, "NoInfo Stewards",
            "Shift approval specialists with access to sensitive volunteer data. They approve signups and handle the operational details that keep events running.",
            "fa-solid fa-clipboard-check"),

        new(RoleNames.Admin, "Full Administrators",
            "The ones who keep the lights on. Full system access, all the keys, all the responsibility. When something breaks at 3am, these are the humans who fix it.",
            "fa-solid fa-gear")
    ];
}

public record StaffRoleDefinition(
    string RoleName,
    string DisplayTitle,
    string Blurb,
    string Icon);

public class StaffRoleSectionViewModel
{
    public string RoleName { get; init; } = string.Empty;
    public string DisplayTitle { get; init; } = string.Empty;
    public string Blurb { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public IReadOnlyList<StaffRoleHolderViewModel> Holders { get; init; } = [];
}

public class StaffRoleHolderViewModel
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? ProfilePictureUrl { get; init; }
}
