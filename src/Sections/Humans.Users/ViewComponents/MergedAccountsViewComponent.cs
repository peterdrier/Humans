using Humans.Users.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>
/// The account this one was folded into and every account folded into it; renders nothing when
/// neither applies, so it can be invoked unconditionally.
/// </summary>
/// <remarks>
/// Reads raw rows: resolving reads answer a tombstone id with the survivor, which is exactly the
/// row this widget must not be handed.
/// </remarks>
public sealed class MergedAccountsViewComponent(IUserService userService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var ct = HttpContext.RequestAborted;
        var info = await userService.GetRawUserInfoAsync(userId, ct);
        if (info is null) return Content(string.Empty);

        var wanted = new HashSet<Guid>(info.MergedUserIds);
        if (info.MergedToUserId is Guid target) wanted.Add(target);
        if (wanted.Count == 0) return Content(string.Empty);

        // Filtering the warmed snapshot costs no query, and the rows must stay raw.
        var rows = (await userService.GetAllRawUserInfosAsync(ct))
            .Where(u => wanted.Contains(u.Id))
            .ToDictionary(u => u.Id);

        MergedAccountViewModel Row(Guid id) => new()
        {
            UserId = id,
            DisplayName = rows.TryGetValue(id, out var row) ? row.BurnerName : null,
            MergedAt = rows.TryGetValue(id, out var at) ? at.MergedAt?.ToDateTimeUtc() : null,
        };

        return View("Default", new MergedAccountsViewModel
        {
            MergedInto = info.MergedToUserId is Guid into ? Row(into) : null,
            MergedAt = info.MergedAt?.ToDateTimeUtc(),
            MergedIn = info.MergedUserIds.Select(Row).OrderBy(r => r.MergedAt).ToList(),
        });
    }
}
