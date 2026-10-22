using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Campaigns;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Campaigns", [
            new("Campaigns", "Campaign", "Index", null, null, "fa-solid fa-bullhorn", PolicyNames.AdminOnly)
        ])
    ];
}
