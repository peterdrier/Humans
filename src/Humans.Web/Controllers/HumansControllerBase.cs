using Humans.Application.Services.AuditLog;
using Humans.Domain.Entities;
using Humans.Web.Constants;
using Humans.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansControllerBase : Controller
{
    private readonly UserManager<User> _userManager;
    protected UserManager<User> UserManager => _userManager;

    protected HumansControllerBase(UserManager<User> userManager)
    {
        _userManager = userManager;
    }

    protected Task<User?> GetCurrentUserAsync()
    {
        return _userManager.GetUserAsync(User);
    }

    protected Task<User?> FindUserByIdAsync(Guid userId)
    {
        return _userManager.FindByIdAsync(userId.ToString());
    }

    protected async Task<(IActionResult? ErrorResult, User User)> RequireCurrentUserAsync()
    {
        return await ResolveCurrentUserAsync(() => NotFound());
    }

    protected async Task<(IActionResult? ErrorResult, User User)> ResolveCurrentUserOrChallengeAsync()
    {
        return await ResolveCurrentUserAsync(() => Challenge());
    }

    protected async Task<(IActionResult? ErrorResult, User User)> ResolveCurrentUserOrUnauthorizedAsync()
    {
        return await ResolveCurrentUserAsync(() => Unauthorized());
    }

    private async Task<(IActionResult? ErrorResult, User User)> ResolveCurrentUserAsync(Func<IActionResult> onMissing)
    {
        var user = await GetCurrentUserAsync();
        return user is null ? (onMissing(), null!) : (null, user);
    }

    protected void SetSuccess(string message)
    {
        TempData[TempDataKeys.SuccessMessage] = message;
    }

    protected void SetError(string message)
    {
        var logger = HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(GetType());
        logger.LogDebug("Error toast: {Message} (Action: {Action})", message, ControllerContext.ActionDescriptor.ActionName);
        TempData[TempDataKeys.ErrorMessage] = message;
    }

    protected void SetInfo(string message)
    {
        TempData[TempDataKeys.InfoMessage] = message;
    }

    protected Task<IdentityResult> UpdateCurrentUserAsync(User user)
    {
        return _userManager.UpdateAsync(user);
    }

    protected IActionResult GoogleSyncAuditView(
        string title,
        string? backUrl,
        string? backLabel,
        IEnumerable<AuditEvent> events)
    {
        return View("GoogleSyncAudit", BuildGoogleSyncAuditViewModel(title, backUrl, backLabel, events));
    }

    protected static GoogleSyncAuditListViewModel BuildGoogleSyncAuditViewModel(
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
