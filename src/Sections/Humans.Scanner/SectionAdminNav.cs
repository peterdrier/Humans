using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Scanner;

/// <summary>Scanner's contribution to the shared "Tickets" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Tickets", [
            new("Scanner", "Scanner", "Index", null, null, "fa-solid fa-qrcode", PolicyNames.ScannerAccess, Weight: 50)
        ], Weight: 0)
    ];
}
