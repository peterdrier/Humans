using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Hosting;

namespace Humans.Development;

/// <summary>Development's admin sidebar contribution — the "Development" group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Development", [
            new("Seed data", "DevSeed", "Index", null, null, "fa-solid fa-seedling", PolicyNames.AdminOnly,
                 EnvironmentGate: env => env.IsDevelopment())
        ])
    ];
}
