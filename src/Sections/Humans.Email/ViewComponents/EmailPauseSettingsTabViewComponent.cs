using Humans.Email.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.ViewComponents;

/// <summary>No admin check here: the tab is contributed under <c>PolicyNames.AdminOnly</c>, so non-admins never get it.</summary>
internal sealed class EmailPauseSettingsTabViewComponent(IEmailOutboxManagement outboxService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var isPaused = await outboxService.IsEmailPausedAsync(HttpContext.RequestAborted);
        return View(new EmailPauseSettingsTabViewModel(isPaused));
    }
}

internal sealed record EmailPauseSettingsTabViewModel(bool IsPaused);
