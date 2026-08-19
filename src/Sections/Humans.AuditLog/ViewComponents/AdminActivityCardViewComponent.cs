using Microsoft.AspNetCore.Mvc;

namespace Humans.AuditLog.ViewComponents;

/// <summary>
/// The "Recent activity" card on the admin dashboard — the last 24h of audit history,
/// gated to <c>PolicyNames.AdminOnly</c>. Contributed into the admin dashboard's
/// <c>admin-dashboard</c> chrome slot (<see cref="Humans.AuditLog.SectionChrome"/>) since
/// nobodies-collective/Humans#1091.
/// </summary>
/// <remarks>
/// Internal — invoked by name through <c>ChromeSlotViewComponent</c>'s
/// <c>Component.InvokeAsync</c>, not a <c>&lt;vc:&gt;</c> tag helper. Its view renders
/// <c>AuditLogViewComponent</c> itself via <c>Component.InvokeAsync</c> rather than the
/// <c>&lt;vc:audit-log&gt;</c> tag, since this section's own <c>_ViewImports.cshtml</c> does
/// not open its own tag helpers.
/// </remarks>
internal sealed class AdminActivityCardViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => View();
}
