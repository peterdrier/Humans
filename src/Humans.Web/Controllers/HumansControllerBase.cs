using Humans.Domain.Entities;
using Humans.Web.Extensions;
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

    protected async Task<(IActionResult? ErrorResult, User User)> ResolveCurrentUserAsync()
    {
        return await ResolveCurrentUserAsync(() => NotFound());
    }

    protected async Task<(IActionResult? ErrorResult, User User)> RequireCurrentUserAsync()
    {
        return await ResolveCurrentUserAsync();
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
        TempData["SuccessMessage"] = message;
    }

    protected void SetError(string message)
    {
        var logger = HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(GetType());
        logger.LogWarning("Error toast: {Message} (Action: {Action})", message, ControllerContext.ActionDescriptor.ActionName);
        TempData["ErrorMessage"] = message;
    }

    protected void SetInfo(string message)
    {
        TempData["InfoMessage"] = message;
    }

    protected Task<IdentityResult> UpdateCurrentUserAsync(User user)
    {
        return _userManager.UpdateAsync(user);
    }

    protected IActionResult GoogleSyncAuditView(
        string title,
        string? backUrl,
        string? backLabel,
        IEnumerable<AuditLogEntry> entries)
    {
        return View("GoogleSyncAudit", BuildGoogleSyncAuditViewModel(title, backUrl, backLabel, entries));
    }

    protected static GoogleSyncAuditListViewModel BuildGoogleSyncAuditViewModel(
        string title,
        string? backUrl,
        string? backLabel,
        IEnumerable<AuditLogEntry> entries)
    {
        return new GoogleSyncAuditListViewModel
        {
            Title = title,
            BackUrl = backUrl,
            BackLabel = backLabel,
            Entries = entries.Select(static entry => new GoogleSyncAuditEntryViewModel
            {
                Action = entry.Action,
                Description = entry.Description,
                UserEmail = entry.UserEmail,
                Role = entry.Role,
                SyncSource = entry.SyncSource,
                OccurredAt = entry.OccurredAt.ToDateTimeUtc(),
                Success = entry.Success,
                ErrorMessage = entry.ErrorMessage,
                ResourceName = entry.Resource?.Name,
                ResourceId = entry.ResourceId,
                RelatedEntityId = entry.RelatedEntityId
            }).ToList()
        };
    }
}
