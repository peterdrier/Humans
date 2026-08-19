using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Mailer;

/// <summary>Mailer's contribution to the shared "Messaging" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Messaging", [
            new("Mailer", "MailerAdmin", "Index", null, null, "fa-solid fa-paper-plane", PolicyNames.AdminOnly, Weight: 20)
        ], Weight: 100)
    ];
}
