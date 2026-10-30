using Humans.Shifts.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.EarlyEntry;

public sealed class Section : ISection, ISectionRoles, ISectionAdminNav
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(EarlyEntryRoles.EarlyEntryAdmin, "Early Entry Admin",
            "Gatekeepers before the gate. They grant, edit and revoke early-entry passes across every team that opens early, so build crews arrive when the plan says.",
            "fa-solid fa-door-open",
            OpensAdminShell: false,
            FormerNames: ["EETeamAdmin"]),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Scoped and uncached: fresh every request, so no contributing section has to evict anything.
        services.AddScoped<IEarlyEntryService, EarlyEntryService>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Early Entry", [
            new("Early entry", "EarlyEntryRoster", "Index", null, null, "fa-solid fa-door-open", ShiftsPolicies.Read)
        ])
    ];
}
