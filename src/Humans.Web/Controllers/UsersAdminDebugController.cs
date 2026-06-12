using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

// Diagnostic surface for UserInfo cache — flat sortable table from GetAllUserInfosAsync, no secondary queries.
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Users/Admin/Debug")]
public sealed class UsersAdminDebugController(IUserServiceRead userService) : HumansControllerBase(userService)
{
    private const int MinPageSize = 10;
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 25;

    private readonly IUserServiceRead _userService = userService;

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

    // Null-first ascending semantics for tri-state booleans — null < false < true.
    private static int NullableBool(bool? b) => b is null ? 0 : b.Value ? 2 : 1;

    private static readonly Dictionary<string, Func<List<UserDebugRow>, IEnumerable<UserDebugRow>>> SortKeys =
        new(StringComparer.Ordinal)
        {
            ["userId"] = rows => rows.OrderBy(r => r.UserId),
            ["hasProfile"] = rows => rows.OrderBy(r => r.HasProfile),
            ["hasTicket"] = rows => rows.OrderBy(r => r.HasTicket),
            ["marketing"] = rows => rows.OrderBy(r => NullableBool(r.MarketingOptedOut)),
            ["burnerName"] = rows => rows.OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase),
            ["legalName"] = rows => rows.OrderBy(r => r.LegalName, StringComparer.OrdinalIgnoreCase),
            ["hasName"] = rows => rows.OrderBy(r => NullableBool(r.HasName)),
            ["hasConsent"] = rows => rows.OrderBy(r => NullableBool(r.HasConsent)),
            ["createdAt"] = rows => rows.OrderBy(r => r.CreatedAt),
            ["lastLoginAt"] = rows => rows.OrderBy(r => r.LastLoginAt ?? NodaTime.Instant.MinValue),
        };

    private static List<UserDebugRow> ApplySort(List<UserDebugRow> rows, string sort, string dir)
    {
        var sorted = SortKeys.TryGetValue(sort, out var sorter)
            ? sorter(rows)
            : rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase);

        return string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase)
            ? sorted.ToList()
            : sorted.Reverse().ToList();
    }
}
