using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public static class AdminHumanListViewModelBuilder
{
    private const int PageSize = 20;

    public static AdminHumanListViewModel Build(
        IReadOnlyList<AdminHumanRow> rows,
        string? search,
        string? filter,
        string? sort,
        string? direction,
        int page,
        Func<Guid, string?> adminDetailUrl)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(adminDetailUrl);

        var normalizedSort = sort?.ToLowerInvariant() ?? "name";
        var ascending = !string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
        var pageNumber = Math.Max(page, 1);

        var sorted = Sort(rows, normalizedSort, ascending);
        var humans = sorted
            .Skip((pageNumber - 1) * PageSize)
            .Take(PageSize)
            .Select(r => new HumanSearchResultViewModel
            {
                UserId = r.UserId,
                BurnerName = r.DisplayName,
                ProfilePictureUrl = r.ProfilePictureUrl,
                AdminEmail = r.Email,
                MembershipStatus = StatusLabel(r.State),
                CreatedAt = r.CreatedAt,
                LastLoginAt = r.LastLoginAt,
                AdminDetailUrl = adminDetailUrl(r.UserId),
            })
            .ToList();

        return new AdminHumanListViewModel
        {
            Humans = humans,
            SearchTerm = search,
            StatusFilter = filter,
            SortBy = normalizedSort,
            SortDir = ascending ? "asc" : "desc",
            TotalCount = rows.Count,
            PageNumber = pageNumber,
            PageSize = PageSize
        };
    }

    private static IOrderedEnumerable<AdminHumanRow> Sort(
        IReadOnlyList<AdminHumanRow> rows,
        string sort,
        bool ascending) =>
        sort switch
        {
            "joined" => ascending
                ? rows.OrderBy(r => r.CreatedAt)
                : rows.OrderByDescending(r => r.CreatedAt),
            "login" => ascending
                ? rows.OrderBy(r => r.LastLoginAt.HasValue ? 0 : 1).ThenBy(r => r.LastLoginAt)
                : rows.OrderBy(r => r.LastLoginAt.HasValue ? 0 : 1).ThenByDescending(r => r.LastLoginAt),
            "status" => ascending
                ? rows.OrderBy(r => StatusLabel(r.State), StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(r => StatusLabel(r.State), StringComparer.OrdinalIgnoreCase),
            _ => ascending
                ? rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

    private static string StatusLabel(UserState state) =>
        state == UserState.DeletePending ? "Delete Pending" : state.ToString();
}
