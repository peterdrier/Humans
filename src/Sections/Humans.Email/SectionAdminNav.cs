using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Email;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Email", [
            new("Preview", "Email", "EmailPreview", null, null, "fa-solid fa-envelope", PolicyNames.AdminOnly, Weight: 0),
            new("Outbox",  "Email", "EmailOutbox",  null, null, "fa-solid fa-inbox",    PolicyNames.AdminOnly, Weight: 10)
        ])
    ];
}
