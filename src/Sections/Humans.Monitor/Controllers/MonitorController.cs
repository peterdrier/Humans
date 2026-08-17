using Humans.GoogleIntegration.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Domain.Enums;
using Humans.Monitor.Contracts;
using Humans.Monitor.Models;
using Humans.UI.Authorization;
using Humans.UI.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Monitor.Controllers;

/// <summary>
/// Operator-facing monitoring of the Google Workspace estate: the on-demand Drive-activity
/// anomaly scan, and the Google-sync audit trail for one resource or one human.
/// </summary>
/// <remarks>
/// These three actions were <c>AuditLogController</c>'s until Monitor was carved out. They
/// moved because two of them inject GoogleIntegration services and AuditLog is a
/// *horizontal* — <c>peters-hard-rules.md</c> forbids a horizontal from referencing a
/// vertical section, and the reference only became visible at the assembly level when
/// GoogleIntegration went to G5. The third (<c>Human</c>) came with them because all three
/// render the same <c>GoogleSync</c> view; splitting them would have split one view, one view
/// model and two helpers across two sections.
///
/// What stayed in AuditLog is the general audit browser (<c>/AuditLog</c>), which reaches no
/// section at all.
/// </remarks>
[Route("Monitor")]
internal sealed class MonitorController(
    IUserServiceRead userService,
    IAuditViewerService auditViewer,
    ILogger<MonitorController> logger) : HumansControllerBase(userService)
{
    private readonly IUserServiceRead _userService = userService;

    [HttpPost("CheckDriveActivity")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckDriveActivity(
        [FromServices] IDriveActivityMonitorService monitorService)
    {
        var currentUser = await GetCurrentUserInfoAsync();

        try
        {
            var count = await monitorService.CheckForAnomalousActivityAsync();
            logger.LogInformation("Board {UserId} triggered manual Drive activity check: {Count} anomalies",
                currentUser?.Id, count);

            SetSuccess(count > 0
                ? $"Drive activity check completed: {count} anomalous change(s) detected."
                : "Drive activity check completed: no anomalies detected.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual Drive activity check failed");
            SetError("Drive activity check failed. Check logs for details.");
        }

        return RedirectToAction("Index", "AuditLog", new { filter = nameof(AuditAction.AnomalousPermissionDetected) });
    }

    [HttpGet("Resource/{id:guid}")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> Resource(
        Guid id,
        [FromServices] ITeamResourceService teamResourceService)
    {
        var resource = await teamResourceService.GetResourceByIdAsync(id);

        if (resource is null)
        {
            return NotFound();
        }

        var events = await auditViewer.GetForResourceAsync(id);
        return GoogleSyncAuditView(
            $"Sync Audit: {resource.Name}",
            Url.Action("Sync", "Google"),
            "Back to Sync Status",
            events);
    }

    [HttpGet("Human/{id:guid}")]
    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    public async Task<IActionResult> Human(Guid id)
    {
        var user = await FindUserInfoByIdAsync(id);

        if (user is null)
        {
            return NotFound();
        }

        var events = await auditViewer.GetGoogleSyncForUserAsync(id);
        var info = await _userService.GetUserInfoAsync(id);
        var displayName = info?.BurnerName ?? user.BurnerName;
        return GoogleSyncAuditView(
            $"Google Sync Audit: {displayName}",
            Url.Action("AdminDetail", "UsersAdmin", new { id }),
            "Back to Human Detail",
            events);
    }

    private IActionResult GoogleSyncAuditView(
        string title,
        string? backUrl,
        string? backLabel,
        IEnumerable<AuditEvent> events)
    {
        return View("GoogleSync", BuildGoogleSyncAuditViewModel(title, backUrl, backLabel, events));
    }

    private static GoogleSyncAuditListViewModel BuildGoogleSyncAuditViewModel(
        string title,
        string? backUrl,
        string? backLabel,
        IEnumerable<AuditEvent> events)
    {
        return new GoogleSyncAuditListViewModel
        {
            Title = title,
            BackUrl = backUrl,
            BackLabel = backLabel,
            Entries = events.Select(static ev => new GoogleSyncAuditEntryViewModel
            {
                Action = ev.Action,
                Description = ev.Description,
                UserEmail = ev.UserEmail,
                Role = ev.Role,
                SyncSource = ev.SyncSource,
                OccurredAt = ev.OccurredAt.ToDateTimeUtc(),
                Success = ev.Success,
                ErrorMessage = ev.ErrorMessage,
                ResourceName = ev.ResourceName,
                ResourceId = ev.ResourceId,
                RelatedEntityId = ev.RelatedEntityId
            }).ToList()
        };
    }
}
