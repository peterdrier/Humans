using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Agent;

/// <summary>Agent's admin sidebar contribution — the "Agent" group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Agent", System: true, Items: [
            new("Status",  "AdminAgent", "Status",        null, null, "fa-solid fa-gauge-high", PolicyNames.AdminOnly),
            new("Config",  "AdminAgent", "Settings",      null, null, "fa-solid fa-robot",      PolicyNames.AdminOnly),
            new("History", "Agent",      "Conversations", null, null, "fa-solid fa-comments",   PolicyNames.AdminOnly)
        ], Weight: 120)
    ];
}
