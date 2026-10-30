using Humans.Base.Attributes;
using Humans.Base;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Gdpr.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Gdpr.Controllers;

[Authorize]
internal sealed class MemberDataController(
    IUserService userService,
    IGdprService gdprService,
    IAccountDeletionService accountDeletionService,
    IClock clock,
    ILogger<MemberDataController> logger,
    IStringLocalizer<GdprResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer) : HumansControllerBase(userService)
{
    [HttpPost("Gdpr/RequestDeletion")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> RequestDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        try
        {
            var flash = DeletionRequestFlash.From(await accountDeletionService.RequestDeletionAsync(user.Id));
            if (flash.Success)
                SetSuccess(flash.Format(localizer));
            else
                SetError(flash.Format(localizer));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process deletion request for user {UserId}", user.Id);
            SetError(localizer["Gdpr_Deletion_RequestFailed"].Value);
        }

        return RedirectToAction("Index", "MyData");
    }

    [HttpGet("Gdpr/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> DownloadData(CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        try
        {
            var export = await gdprService.ExportForUserAsync(user.Id, ct);

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
            SetError(sharedLocalizer["Error_TryAgainLater"].Value);
            return RedirectToAction("Index", "MyData");
        }
    }
}
