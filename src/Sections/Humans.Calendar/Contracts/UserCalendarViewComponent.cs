using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Calendar.Contracts;

/// <summary>
/// Admin visibility into a user's personal iCal feed: renders the same
/// aggregated items the /api/ical endpoint serializes (one code path via
/// IICalFeedService). Never shows the secret token or feed URL.
/// </summary>
/// <remarks>
/// Public, under <c>Contracts/</c>, so MVC's default provider discovers it and the
/// tag helper is generated — Shell's widget gallery renders it as
/// <c>&lt;vc:user-calendar&gt;</c> and Users' admin detail invokes it by name. An
/// internal view component ships the element as inert literal markup with a green
/// build (design §15 step 6; HUM0034's carve-out is the folder).
/// </remarks>
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
