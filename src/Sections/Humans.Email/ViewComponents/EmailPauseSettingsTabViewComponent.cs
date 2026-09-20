using Humans.Email.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.ViewComponents;

/// <summary>
/// The /Settings#email tab (peterdrier/Humans#1634): the send-pause toggle that used to
/// live on the /Email/EmailOutbox dashboard header. <see cref="SectionSettings"/>
/// contributes this tab with <c>PolicyNames.AdminOnly</c>, so composition already keeps
/// a non-admin from seeing it — no further check needed here.
/// </summary>
internal sealed class EmailPauseSettingsTabViewComponent(IEmailOutboxService outboxService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var isPaused = await outboxService.IsEmailPausedAsync(HttpContext.RequestAborted);
        return View(new EmailPauseSettingsTabViewModel(isPaused));
    }
}

internal sealed record EmailPauseSettingsTabViewModel(bool IsPaused);
