using Humans.Application.Interfaces.Users;
using Humans.UI.Authorization;
using Humans.UI.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Users.Contracts;

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
    private static int NullableBoolRank(bool? b) => b is null ? 0 : b.Value ? 2 : 1;

    // Sort-key → column comparison. A dictionary of comparisons (rather than a
    // switch dispatching to OrderBy) keeps ApplySort itself a straight-line
    // lookup + sort — see memory/architecture/display-sort-in-controllers.md
    // (sorting is legitimately controller turf; this only reshapes it).
    // Ordinal (case-sensitive) keys — matches the original switch's exact-string-literal semantics.
    private static readonly IReadOnlyDictionary<string, Comparison<UserDebugRow>> SortComparisons =
        new Dictionary<string, Comparison<UserDebugRow>>(StringComparer.Ordinal)
        {
            ["userId"] = (a, b) => a.UserId.CompareTo(b.UserId),
            ["hasProfile"] = (a, b) => a.HasProfile.CompareTo(b.HasProfile),
            ["hasTicket"] = (a, b) => a.HasTicket.CompareTo(b.HasTicket),
            ["marketing"] = (a, b) => NullableBoolRank(a.MarketingOptedOut).CompareTo(NullableBoolRank(b.MarketingOptedOut)),
            ["burnerName"] = (a, b) => string.Compare(a.BurnerName, b.BurnerName, StringComparison.OrdinalIgnoreCase),
            ["legalName"] = (a, b) => string.Compare(a.LegalName, b.LegalName, StringComparison.OrdinalIgnoreCase),
            ["hasName"] = (a, b) => NullableBoolRank(a.HasName).CompareTo(NullableBoolRank(b.HasName)),
            ["hasConsent"] = (a, b) => NullableBoolRank(a.HasConsent).CompareTo(NullableBoolRank(b.HasConsent)),
            ["createdAt"] = (a, b) => a.CreatedAt.CompareTo(b.CreatedAt),
            ["lastLoginAt"] = (a, b) => (a.LastLoginAt ?? Instant.MinValue).CompareTo(b.LastLoginAt ?? Instant.MinValue),
        };

    private static List<UserDebugRow> ApplySort(List<UserDebugRow> rows, string sort, string dir)
    {
        var asc = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        var comparison = SortComparisons.GetValueOrDefault(sort,
            (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        // OrderBy (not List.Sort) — stable sort, matching the prior switch/OrderBy behavior for ties.
        var sorted = rows.OrderBy(r => r, Comparer<UserDebugRow>.Create(comparison)).ToList();
        if (!asc) sorted.Reverse();
        return sorted;
    }
}
