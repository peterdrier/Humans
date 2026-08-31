using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Campaigns;

/// <summary>Campaigns' contribution to the shared "Tickets" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Tickets", [
            // Campaigns distribute ticket-vendor discount codes (email is just the
            // delivery channel) — Tickets, not Messaging. See src/Sections/Humans.Campaigns/Docs/Campaigns.md.
            new("Campaigns", "Campaign", "Index", null, null, "fa-solid fa-bullhorn", PolicyNames.AdminOnly, Weight: 40)
        ], Weight: 0)
    ];
}
