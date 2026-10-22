using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Scanner;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Scanner", [
            new("Scanner", "Scanner", "Index", null, null, "fa-solid fa-qrcode", PolicyNames.ScannerAccess)
        ])
    ];
}
