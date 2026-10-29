using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Agent;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Agent", [
            new("Status",  "AdminAgent", "Status",        null, null, "fa-solid fa-gauge-high", PolicyNames.BoardOrAdmin),
            new("History", "Agent",      "Conversations", null, null, "fa-solid fa-comments",   PolicyNames.BoardOrAdmin)
        ])
    ];
}
