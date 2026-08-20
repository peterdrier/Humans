using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Hosting;

namespace Humans.Development;

/// <summary>Development's admin sidebar contribution — the "Dev" group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Dev", System: true, Items: [
            new("Seed budget",     "DevSeed", "SeedBudget",    null, null, "fa-solid fa-coins",    PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction()),
            new("Seed camp roles", "DevSeed", "SeedCampRoles", null, null, "fa-solid fa-user-tag", PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction())
        ], Weight: 150)
    ];
}
