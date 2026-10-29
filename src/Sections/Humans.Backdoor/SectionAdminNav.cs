using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Backdoor;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Backdoor", [
            new("API keys", "Backdoor", "Index", null, null, "fa-solid fa-key", PolicyNames.BoardOrAdmin)
        ])
    ];
}
