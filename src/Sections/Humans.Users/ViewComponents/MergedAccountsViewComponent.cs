using Humans.Users.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>
/// The merge picture for one account on an admin screen: the account it was folded into
/// if it is a tombstone, and every account folded into it. Renders nothing when neither
/// applies, so an admin detail page can invoke it unconditionally.
/// </summary>
/// <remarks>
/// Reads raw rows (<see cref="IUserService.GetRawUserInfoAsync"/>), because the merge
/// picture is the one thing the resolving reads take away: they answer a tombstone id
/// with the survivor, which is exactly the row this widget must not be handed.
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
