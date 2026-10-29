using Humans.Email.Contracts;
using Humans.Consent.Contracts;
using Humans.Debug.Models;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;

namespace Humans.Debug.Controllers;

// UserInfo cache diagnostic: tombstones included, since it shows what is in the cache.
[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Debug/Users")]
internal sealed class UsersDebugController(
    IUserService userService, IConsentService consentService, ITicketService tickets,
    ICommunicationPreferenceService preferences)
    : HumansControllerBase(userService)
{
    private const int MinPageSize = 10;
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 25;

    private readonly IUserService _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1, int pageSize = DefaultPageSize,
                               string sort = "displayName", string dir = "asc",
                               CancellationToken ct = default)
    {
        pageSize = pageSize.ClampPageSize(MinPageSize, MaxPageSize);
        if (page < 1) page = 1;

        var snapshot = await _userService.GetAllRawUserInfosAsync(ct);
        var reviews = await consentService.GetConsentReviewsAsync(ct);
        // Any-year Ticketed/Attended participation — diagnostic only.
        var ticketHolders = (await tickets.GetAllParticipationsAsync(ct))
            .Where(p => p.HasTicket).Select(p => p.UserId).ToHashSet();
        var marketing = await preferences.GetPreferencesForCategoryAsync(MessageCategory.Marketing, ct);
        var allRows = snapshot
            .Select(u => UserDebugRow.From(u, reviews.GetValueOrDefault(u.Id), ticketHolders.Contains(u.Id),
                marketing.GetValueOrDefault(u.Id)?.OptedOut))
            .ToList();

        var sorted = ApplySort(allRows, sort, dir);
        var total = sorted.Count;
        var paged = sorted.Skip((int)Math.Min(((long)page - 1) * pageSize, total)).Take(pageSize).ToList();

        return View(new UsersDebugViewModel(paged, total, page, pageSize, sort, dir));
    }

    // Null-first ascending semantics for tri-state booleans — null < false < true.
    private static int NullableBoolRank(bool? b) => b is null ? 0 : b.Value ? 2 : 1;

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

        // OrderBy, not List.Sort: stable for ties.
        var sorted = rows.OrderBy(r => r, Comparer<UserDebugRow>.Create(comparison)).ToList();
        if (!asc) sorted.Reverse();
        return sorted;
    }
}
