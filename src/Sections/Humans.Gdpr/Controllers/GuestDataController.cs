using System.Globalization;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Base;
using Humans.Gdpr.Contracts;
using Humans.Gdpr.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Gdpr.Controllers;

[Authorize]
internal sealed class GuestDataController(
    IUserService userService,
    IGdprService gdprService,
    IAccountDeletionService accountDeletionService,
    IClock clock,
    ILogger<GuestDataController> logger,
    IStringLocalizer<SharedResource> sharedLocalizer,
    IStringLocalizer<GdprResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("Gdpr/Guest/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData(CancellationToken ct)
    {
        var (noCurrentUser, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (noCurrentUser is not null)
            return noCurrentUser;

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
            return RedirectToAction("Index", "Guest");
        }
    }

    [HttpPost("Gdpr/Guest/RequestDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return Challenge();

        try
        {
            var result = await accountDeletionService.RequestDeletionAsync(user.Id);
            var flash = GuestDeletionRequestFlash.From(result);
            if (flash.Success)
                SetSuccess(FormatDeletionFlash(flash));
            else
                SetError(FormatDeletionFlash(flash));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process deletion request for user {UserId}", user.Id);
            SetError(localizer["Gdpr_Guest_DeletionRequestFailed"].Value);
        }

        return RedirectToAction("Index", "Guest");
    }

    [HttpPost("Gdpr/Guest/CancelDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return Challenge();

        var result = await accountDeletionService.CancelDeletionAsync(user.Id);
        if (!result.Success)
        {
            SetError(string.Equals(result.ErrorKey, "NoDeletionPending", StringComparison.Ordinal)
                ? localizer["Gdpr_Deletion_NotPending"].Value
                : localizer["Gdpr_Guest_CancelDeletionFailed"].Value);
            return RedirectToAction("Index", "Guest");
        }

        SetSuccess(localizer["Gdpr_Deletion_Cancelled"].Value);
        return RedirectToAction("Index", "Guest");
    }

    private string FormatDeletionFlash(GuestDeletionRequestFlash flash) =>
        string.Format(CultureInfo.CurrentCulture, localizer[flash.ResourceKey].Value, flash.EffectiveDeletionDate.ToDate());
}
