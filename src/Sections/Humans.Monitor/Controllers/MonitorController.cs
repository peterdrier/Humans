using Humans.AuditLog.Contracts;
using Humans.Monitor.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;
using Humans.GoogleIntegration.Contracts;

namespace Humans.Monitor.Controllers;

[Route("Monitor")]
internal sealed class MonitorController(
    IUserService userService,
    ILogger<MonitorController> logger) : HumansControllerBase(userService)
{
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

        return RedirectToAction("Index", "AuditLog", new { filter = GoogleIntegrationAuditActions.AnomalousPermissionDetected.Name });
    }
}
