using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Backdoor;

/// <summary>
/// Backdoor's admin entry, alongside Debug's other system pages: where the personal keys
/// that open <c>/api/backdoor/*</c> are allocated, rotated and revoked.
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Diagnostics", System: true, Items: [
            // Weight 60 lands it between Timings (50) and Configuration (70).
            new("API keys", "Backdoor", "Index", null, null, "fa-solid fa-key", PolicyNames.AdminOnly, Weight: 60)
        ], Weight: 140)
    ];
}
