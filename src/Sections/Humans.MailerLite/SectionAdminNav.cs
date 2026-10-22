using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.MailerLite;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("MailerLite", [
            new("MailerLite", "MailerLiteAdmin", "Index", null, null, "fa-solid fa-paper-plane", PolicyNames.AdminOnly)
        ])
    ];
}
