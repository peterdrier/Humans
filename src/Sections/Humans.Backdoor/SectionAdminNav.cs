using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Backdoor;

/// <summary>
/// Backdoor's admin sidebar group: where the personal keys
/// that open <c>/api/backdoor/*</c> are allocated, rotated and revoked.
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Backdoor", [
            new("API keys", "Backdoor", "Index", null, null, "fa-solid fa-key", PolicyNames.AdminOnly)
        ])
    ];
}
