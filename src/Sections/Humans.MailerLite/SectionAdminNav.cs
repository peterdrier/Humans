using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.MailerLite;

/// <summary>MailerLite's contribution to the shared "Messaging" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Messaging", [
            new("MailerLite", "MailerLiteAdmin", "Index", null, null, "fa-solid fa-paper-plane", PolicyNames.AdminOnly, Weight: 20)
        ], Weight: 100)
    ];
}
