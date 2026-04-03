using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class AuditLogViewComponent : ViewComponent
{
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<AuditLogViewComponent> _logger;

    public AuditLogViewComponent(
        IAuditLogService auditLogService,
        ILogger<AuditLogViewComponent> logger)
    {
        _auditLogService = auditLogService;
        _logger = logger;
    }

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
            model.Entries = await _auditLogService.GetFilteredEntriesAsync(
                entityType, entityId, userId, actionList, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading audit log entries for EntityType={EntityType}, EntityId={EntityId}, UserId={UserId}",
                entityType, entityId, userId);
        }

        return View(model);
    }
}

public class AuditLogComponentViewModel
{
    public string Title { get; set; } = "Audit History";
    public bool ShowCard { get; set; } = true;
    public IReadOnlyList<Domain.Entities.AuditLogEntry> Entries { get; set; } = [];
}
