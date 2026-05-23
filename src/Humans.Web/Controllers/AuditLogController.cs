using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.AuditLog;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Route("AuditLog")]
public class AuditLogController(
    IUserServiceRead userService,
    IAuditViewerService auditViewer,
    ILogger<AuditLogController> logger) : HumansControllerBase(userService)
{
    private readonly IUserServiceRead _userService = userService;

    [HttpGet("")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> Index(string? filter, int page = 1)
    {
        var pageSize = 50;
        var result = await auditViewer.GetPageAsync(filter, page, pageSize);

        var viewModel = new AuditLogListViewModel
        {
            Events = result.Items,
            ActionFilter = filter,
            AnomalyCount = result.AnomalyCount,
            TotalCount = result.TotalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }

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

        return RedirectToAction(nameof(Index), new { filter = nameof(AuditAction.AnomalousPermissionDetected) });
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
            Url.Action(nameof(GoogleController.Sync), "Google"),
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
            Url.Action(nameof(ProfileController.AdminDetail), "Profile", new { id }),
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
