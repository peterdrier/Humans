using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Diagnostic debug surface for the in-memory <see cref="UserInfo"/> cache.
/// Flat paginated/sortable table of every cached user — used to verify the
/// cache holds the expected data after imports and migrations. Every column
/// comes from <see cref="IUserService.GetAllUserInfos"/>; nothing on this
/// page makes a secondary query.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Users/Admin/Debug")]
public sealed class UsersAdminDebugController : Controller
{
    private const int MinPageSize = 10;
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    private readonly IUserService _userService;

    public UsersAdminDebugController(IUserService userService)
    {
        _userService = userService;
    }

    [HttpGet("")]
    public IActionResult Index(int page = 1, int pageSize = DefaultPageSize,
                               string sort = "displayName", string dir = "asc")
    {
        pageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);
        if (page < 1) page = 1;

        var snapshot = _userService.GetAllUserInfos();
        var allRows = snapshot.Select(UserDebugRow.From).ToList();

        var sorted = ApplySort(allRows, sort, dir);
        var total = sorted.Count;
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return View(new UsersDebugViewModel(paged, total, page, pageSize, sort, dir));
    }

    private static List<UserDebugRow> ApplySort(List<UserDebugRow> rows, string sort, string dir)
    {
        var asc = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);

        // Null-first ascending semantics for tri-state booleans — null < false < true.
        static int NullableBool(bool? b) => b is null ? 0 : b.Value ? 2 : 1;

        IEnumerable<UserDebugRow> sorted = sort switch
        {
            "userId"      => rows.OrderBy(r => r.UserId),
            "hasProfile"  => rows.OrderBy(r => r.HasProfile),
            "hasTicket"   => rows.OrderBy(r => r.HasTicket),
            "marketing"   => rows.OrderBy(r => NullableBool(r.MarketingOptedOut)),
            "burnerName"  => rows.OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase),
            "legalName"   => rows.OrderBy(r => r.LegalName, StringComparer.OrdinalIgnoreCase),
            "hasConsent"  => rows.OrderBy(r => NullableBool(r.HasConsent)),
            _             => rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

        return asc ? sorted.ToList() : sorted.Reverse().ToList();
    }
}
