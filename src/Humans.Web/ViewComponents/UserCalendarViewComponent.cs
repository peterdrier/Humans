using Humans.Application.Interfaces.ICalFeed;
using Humans.Application.Interfaces.Users;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Admin visibility into a user's personal iCal feed: renders the same
/// aggregated items the /api/ical endpoint serializes (one code path via
/// IICalFeedService). Never shows the secret token or feed URL.
/// </summary>
public sealed class UserCalendarViewComponent(
    IICalFeedService feed,
    IUserServiceRead users,
    ILogger<UserCalendarViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var model = new UserCalendarViewModel();
        try
        {
            var ct = HttpContext.RequestAborted;
            var user = await users.GetUserInfoAsync(userId, ct);
            model.HasFeedToken = user?.ICalToken is not null;
            model.Items = await feed.GetFeedItemsAsync(userId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading calendar feed items for user {UserId}", userId);
            model.LoadFailed = true;
        }
        return View(model);
    }
}

public class UserCalendarViewModel
{
    public bool HasFeedToken { get; set; }
    public bool LoadFailed { get; set; }
    public IReadOnlyList<CalendarFeedItem> Items { get; set; } = [];
}
