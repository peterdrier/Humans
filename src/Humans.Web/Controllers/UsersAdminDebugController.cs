using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

// Diagnostic surface for UserInfo cache — flat sortable table from GetAllUserInfosAsync, no secondary queries.
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Users/Admin/Debug")]
public sealed class UsersAdminDebugController : HumansControllerBase
{
    private const int MinPageSize = 10;
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 25;

    private readonly IUserService _userService;

    public UsersAdminDebugController(IUserService userService)
        : base(userService)
    {
        _userService = userService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1, int pageSize = DefaultPageSize,
                               string sort = "displayName", string dir = "asc",
                               CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);
        if (page < 1) page = 1;

        var snapshot = await _userService.GetAllUserInfosAsync(ct);
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
            "userId" => rows.OrderBy(r => r.UserId),
            "hasProfile" => rows.OrderBy(r => r.HasProfile),
            "hasTicket" => rows.OrderBy(r => r.HasTicket),
            "marketing" => rows.OrderBy(r => NullableBool(r.MarketingOptedOut)),
            "burnerName" => rows.OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase),
            "legalName" => rows.OrderBy(r => r.LegalName, StringComparer.OrdinalIgnoreCase),
            "hasName" => rows.OrderBy(r => NullableBool(r.HasName)),
            "hasConsent" => rows.OrderBy(r => NullableBool(r.HasConsent)),
            "createdAt" => rows.OrderBy(r => r.CreatedAt),
            "lastLoginAt" => rows.OrderBy(r => r.LastLoginAt ?? NodaTime.Instant.MinValue),
            _ => rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

        return asc ? sorted.ToList() : sorted.Reverse().ToList();
    }
}
