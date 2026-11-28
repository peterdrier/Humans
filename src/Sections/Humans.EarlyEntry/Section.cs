using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.EarlyEntry.Controllers;

namespace Humans.EarlyEntry;

public sealed class Section : SectionBase, ISectionRoles, ISectionAdminNav, ISectionPolicies
{
    public override string DisplayName => "Early Entry";

    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(EarlyEntryRoles.EarlyEntryAdmin, "Early Entry Admin",
            "Gatekeepers before the gate. They grant, edit and revoke early-entry passes across every team that opens early, so build crews arrive when the plan says.",
            "fa-solid fa-door-open",
            FormerNames: ["EETeamAdmin"]),
    ];

    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Scoped and uncached: fresh every request, so no contributing section has to evict anything.
        services.AddScoped<IEarlyEntryService, EarlyEntryService>();
    }

    void ISectionPolicies.AddPolicies(AuthorizationOptions options) =>
        options.AddPolicy(EarlyEntryPolicies.Roster, policy =>
            policy.RequireRole(RoleNames.Admin, RoleNames.Board, EarlyEntryRoles.EarlyEntryAdmin));

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        AdminGroup([
            AdminLink<EarlyEntryRosterController>("Early entry", nameof(EarlyEntryRosterController.Index), "fa-solid fa-door-open", EarlyEntryPolicies.Roster)
        ])
    ];
}
