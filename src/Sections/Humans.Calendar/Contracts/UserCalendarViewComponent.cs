using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Calendar.Contracts;

/// <summary>Admin view of a member's feed items. Never shows the token or feed URL.</summary>
/// <remarks>
/// Public so MVC discovers it and generates the tag helper: an internal view component renders as inert markup
/// with a green build.
/// </remarks>
public sealed class UserCalendarViewComponent(
    IICalFeedService feed,
    IUserService users,
    ILogger<UserCalendarViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var model = new UserCalendarViewModel();
        try
        {
            var ct = HttpContext.RequestAborted;
            var user = await users.GetUserInfoAsync(userId, ct);
            model.HasFeedToken = await feed.HasFeedAsync(user?.Id ?? userId, ct);
            model.Items = (await feed.GetFeedItemsAsync(user?.Id ?? userId, ct)).OrderBy(i => i.Start).ToList();
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
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
