using Humans.AuditLog.Contracts;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Humans.AuditLog.ViewComponents;

/// <summary>
/// <c>&lt;vc:audit-log&gt;</c> — renders audit history on any page. Public, and every
/// consuming assembly must carry <c>@addTagHelper *, Humans.AuditLog</c> in its
/// <c>_ViewImports.cshtml</c>: a <c>&lt;vc:&gt;</c> tag with no matching tag helper ships as
/// inert literal markup with a green build and no runtime error.
/// </summary>
public class AuditLogViewComponent(IAuditViewerService auditViewer, ILogger<AuditLogViewComponent> logger)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        string? actions = null,
        int limit = 20,
        string title = "Audit History",
        bool showCard = true)
    {
        IReadOnlyList<AuditAction>? actionList = null;
        if (!string.IsNullOrWhiteSpace(actions))
        {
            actionList = actions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => Enum.TryParse<AuditAction>(a, ignoreCase: true, out var parsed) ? (AuditAction?)parsed : null)
                .Where(a => a.HasValue)
                .Select(a => a!.Value)
                .ToList();
        }

        var model = new AuditLogComponentViewModel
        {
            Title = title,
            ShowCard = showCard
        };

        try
        {
            model.Events = await auditViewer.GetFilteredAsync(
                entityType, entityId, userId, actionList, limit);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading audit log entries for EntityType={EntityType}, EntityId={EntityId}, UserId={UserId}",
                entityType, entityId, userId);
        }

        return View(model);
    }
}

internal sealed class AuditLogComponentViewModel
{
    public string Title { get; set; } = "Audit History";
    public bool ShowCard { get; set; } = true;
    public IReadOnlyList<AuditEvent> Events { get; set; } = [];
}
