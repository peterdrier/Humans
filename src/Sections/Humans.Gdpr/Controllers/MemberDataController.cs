using System.Globalization;
using Humans.Base;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Gdpr.Controllers;

[Authorize]
internal sealed class MemberDataController(
    IUserService userService,
    IGdprService gdprExportService,
    IAccountDeletionService accountDeletionService,
    IClock clock,
    ILogger<MemberDataController> logger,
    IStringLocalizer<GdprResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer) : HumansControllerBase(userService)
{
    [HttpPost("Gdpr/RequestDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var result = await accountDeletionService.RequestDeletionAsync(user.Id);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "AlreadyPending", StringComparison.Ordinal))
                SetError(localizer["Gdpr_Deletion_AlreadyPending"].Value);
            return RedirectToAction("Index", "MyData");
        }

        SetSuccess(string.Format(CultureInfo.CurrentCulture,
            localizer["Gdpr_Deletion_Requested"].Value,
            result.EffectiveDeletionDate?.ToDateTimeUtc().ToDate() ?? ""));
        return RedirectToAction("Index", "MyData");
    }

    [HttpGet("Gdpr/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData(CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        try
        {
            var export = await gdprExportService.ExportForUserAsync(user.Id, ct);

            var bytes = GdprExportSerializer.Serialize(export);
            var fileName = $"nobodies-profiles-export-{clock.GetCurrentInstant().ToDateTimeUtc().ToInvariantDate()}.json";

            return File(bytes, "application/json", fileName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export data for user {UserId}", user.Id);
            SetError(sharedLocalizer["Error_TryAgainLater"].Value);
            return RedirectToAction("Index", "MyData");
        }
    }
}
