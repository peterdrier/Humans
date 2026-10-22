using Microsoft.AspNetCore.Mvc;

namespace Humans.AuditLog.ViewComponents;

/// <summary>The last 24h of audit history, AdminOnly. Invoked by Type through the chrome slot, not a tag helper.</summary>
internal sealed class AdminActivityCardViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => View();
}
