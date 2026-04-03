using Humans.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders a nobodies.team email badge or status indicator for a user.
/// Uses IMemoryCache with short TTL to prevent N+1 queries on list pages.
///
/// Modes:
///   "badge"     — icon badge (ProfileCard)
///   "status"    — warning badge when email not primary (AdminList)
///   "email"     — show actual email address
///   "provision" — show email or provisioning form (TeamAdmin/Members)
///   "detail"    — show email + linked badge, or provisioning form (AdminDetail)
/// </summary>
public class NobodiesEmailBadgeViewComponent : ViewComponent
{
    private readonly IUserEmailService _userEmailService;
    private readonly IMemoryCache _cache;

    public const string CacheKey = "NobodiesTeamEmails_All";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    public NobodiesEmailBadgeViewComponent(
        IUserEmailService userEmailService,
        IMemoryCache cache)
    {
        _userEmailService = userEmailService;
        _cache = cache;
    }

    /// <summary>
    /// Renders a nobodies.team email badge for the given user.
    /// </summary>
    /// <param name="userId">The user to check.</param>
    /// <param name="mode">Display mode — see class doc.</param>
    /// <param name="teamSlug">Team slug for provisioning form (provision mode only).</param>
    /// <param name="displayName">User display name for confirm dialog (provision mode only).</param>
    public async Task<IViewComponentResult> InvokeAsync(
        Guid userId,
        string mode = "badge",
        string? teamSlug = null,
        string? displayName = null)
    {
        var allStatuses = await GetCachedStatusesAsync();

        var hasEmail = allStatuses.TryGetValue(userId, out var info);

        ViewBag.UserId = userId;
        ViewBag.HasEmail = hasEmail;
        ViewBag.Email = hasEmail ? info.Email : null;
        ViewBag.IsPrimary = hasEmail && info.IsPrimary;
        ViewBag.Mode = mode;
        ViewBag.TeamSlug = teamSlug;
        ViewBag.DisplayName = displayName;

        return View();
    }

    private async Task<Dictionary<Guid, (string Email, bool IsPrimary)>> GetCachedStatusesAsync()
    {
        if (_cache.TryGetValue(CacheKey, out Dictionary<Guid, (string Email, bool IsPrimary)>? cached) && cached is not null)
            return cached;

        // Load all nobodies.team email statuses at once — fine at ~500 users
        var statusByUser = await _userEmailService.GetNobodiesTeamEmailStatusByUserAsync();

        // Also load actual email addresses for users who have them
        var userIds = statusByUser.Keys.ToList();
        var emailsByUser = userIds.Count > 0
            ? await _userEmailService.GetNobodiesTeamEmailsByUserIdsAsync(userIds)
            : new Dictionary<Guid, string>();

        var result = new Dictionary<Guid, (string Email, bool IsPrimary)>();
        foreach (var (uid, isPrimary) in statusByUser)
        {
            if (emailsByUser.TryGetValue(uid, out var email))
            {
                result[uid] = (email, isPrimary);
            }
        }

        _cache.Set(CacheKey, result, CacheTtl);
        return result;
    }
}
