using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Email;

/// <summary>Email's contribution to the shared "Messaging" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Messaging", [
            new("Email preview", "Email", "EmailPreview", null, null, "fa-solid fa-envelope", PolicyNames.AdminOnly, Weight: 0),
            new("Email outbox",  "Email", "EmailOutbox",  null, null, "fa-solid fa-inbox",    PolicyNames.AdminOnly, Weight: 10)
        ], Weight: 100)
    ];
}
