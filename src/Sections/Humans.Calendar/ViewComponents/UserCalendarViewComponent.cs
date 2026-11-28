using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;
using Humans.Calendar.Contracts;
using Humans.Calendar.Services;

namespace Humans.Calendar.ViewComponents;

/// <summary>Admin view of a member's feed items. Never shows the token or feed URL.</summary>
internal sealed class UserCalendarViewComponent(
    IICalFeedService feed,
    IUserService users,
    ICalendarFeedTokenService tokens,
    ILogger<UserCalendarViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var model = new UserCalendarViewModel();
        try
        {
            var ct = HttpContext.RequestAborted;
            var user = await users.GetUserInfoAsync(userId, ct);
            model.HasFeedToken = await tokens.GetAsync(user?.Id ?? userId, ct) is not null;
            model.Items = (await feed.GetFeedItemsAsync(user?.Id ?? userId, ct)).OrderBy(i => i.Start).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "Error loading calendar feed items for user {UserId}", userId);
            model.LoadFailed = true;
        }
        return View(model);
    }
}

internal sealed class UserCalendarViewModel
{
    public bool HasFeedToken { get; set; }
    public bool LoadFailed { get; set; }
    public IReadOnlyList<CalendarFeedItem> Items { get; set; } = [];
}
