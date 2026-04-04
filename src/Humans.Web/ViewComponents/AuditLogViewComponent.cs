using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Humans.Web.ViewComponents;

public class AuditLogViewComponent : ViewComponent
{
    private readonly IAuditLogService _auditLogService;
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<AuditLogViewComponent> _logger;

    public AuditLogViewComponent(
        IAuditLogService auditLogService,
        HumansDbContext dbContext,
        ILogger<AuditLogViewComponent> logger)
    {
        _auditLogService = auditLogService;
        _dbContext = dbContext;
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

            // Batch-load display names for all referenced user IDs
            // (actors, subjects via EntityId for User/Profile types, and RelatedEntityId for User types)
            var userIds = model.Entries
                .SelectMany(e => new[]
                {
                    e.ActorUserId,
                    e.EntityType is "User" or "Profile" or "WorkspaceAccount" ? (Guid?)e.EntityId : null,
                    string.Equals(e.RelatedEntityType, "User", StringComparison.Ordinal) ? e.RelatedEntityId : null
                })
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            if (userIds.Count > 0)
            {
                model.UserDisplayNames = await _dbContext.Users
                    .AsNoTracking()
                    .Where(u => userIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u.DisplayName);
            }

            // Batch-load team names for entries that reference teams
            var teamIds = model.Entries
                .SelectMany(e => new[]
                {
                    string.Equals(e.EntityType, "Team", StringComparison.Ordinal) ? (Guid?)e.EntityId : null,
                    string.Equals(e.RelatedEntityType, "Team", StringComparison.Ordinal) ? e.RelatedEntityId : null
                })
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            if (teamIds.Count > 0)
            {
                model.TeamNames = await _dbContext.Teams
                    .AsNoTracking()
                    .Where(t => teamIds.Contains(t.Id))
                    .ToDictionaryAsync(t => t.Id, t => (t.Name, t.Slug));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading audit log entries for EntityType={EntityType}, EntityId={EntityId}, UserId={UserId}",
                entityType, entityId, userId);
        }

        return View(model);
    }

    /// <summary>
    /// Maps an AuditAction to a short verb phrase for structured display.
    /// Returns null for actions that should fall back to Description text.
    /// </summary>
    public static string? GetActionVerb(AuditAction action) => action switch
    {
        AuditAction.TeamMemberAdded => "added",
        AuditAction.TeamMemberRemoved => "removed",
        AuditAction.TeamMemberRoleChanged => "changed role for",
        AuditAction.TeamJoinedDirectly => "joined",
        AuditAction.TeamLeft => "left",
        AuditAction.TeamJoinRequestApproved => "approved join request for",
        AuditAction.TeamJoinRequestRejected => "rejected join request for",
        AuditAction.MemberSuspended => "suspended",
        AuditAction.MemberUnsuspended => "unsuspended",
        AuditAction.VolunteerApproved => "approved",
        AuditAction.RoleAssigned => "assigned role to",
        AuditAction.RoleEnded => "ended role for",
        AuditAction.ConsentCheckCleared => "cleared consent check for",
        AuditAction.ConsentCheckFlagged => "flagged consent check for",
        AuditAction.SignupRejected => "rejected signup for",
        AuditAction.TierApplicationApproved => "approved tier application for",
        AuditAction.TierApplicationRejected => "rejected tier application for",
        _ => null
    };
}

public class AuditLogComponentViewModel
{
    public string Title { get; set; } = "Audit History";
    public bool ShowCard { get; set; } = true;
    public IReadOnlyList<Domain.Entities.AuditLogEntry> Entries { get; set; } = [];
    public Dictionary<Guid, string> UserDisplayNames { get; set; } = new();
    public Dictionary<Guid, (string Name, string Slug)> TeamNames { get; set; } = new();
}
