using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Base;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Gdpr.Controllers;

/// <summary>
/// Article 15 data export for profileless accounts (authenticated users without a
/// Profile); lives in Gdpr because its only data surface is
/// <see cref="IGdprService"/>.
/// </summary>
[Authorize]
internal sealed class GuestDataController(
    IUserServiceRead userService,
    IGdprService gdprExportService,
    IClock clock,
    ILogger<GuestDataController> logger,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("Guest/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData(CancellationToken ct)
    {
        var (noCurrentUser, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (noCurrentUser is not null)
            return noCurrentUser;

        try
        {
            var export = await gdprExportService.ExportForUserAsync(user.Id, ct);

            var bytes = GdprExportSerializer.Serialize(export);
            var fileName = $"nobodies-data-export-{clock.GetCurrentInstant().ToDateTimeUtc().ToInvariantDate()}.json";

            return File(bytes, "application/json", fileName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export data for user {UserId}", user.Id);
            SetError(localizer["Error_TryAgainLater"].Value);
            return RedirectToAction("Index", "Guest");
        }
    }
}
