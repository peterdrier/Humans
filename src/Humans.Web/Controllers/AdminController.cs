using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Infrastructure.Data;
using Humans.Web.Models;
using Humans.Application.Interfaces.Onboarding;

namespace Humans.Web.Controllers;

[Route("Admin")]
public class AdminController : HumansControllerBase
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<AdminController> _logger;
    private readonly IWebHostEnvironment _environment;
    private readonly IOnboardingService _onboardingService;
    private readonly ConfigurationRegistry _configRegistry;
    private readonly QueryStatistics _queryStatistics;
    private readonly ICacheStatsProvider _cacheStatsProvider;

    public AdminController(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ILogger<AdminController> logger,
        IWebHostEnvironment environment,
        IOnboardingService onboardingService,
        ConfigurationRegistry configRegistry,
        QueryStatistics queryStatistics,
        ICacheStatsProvider cacheStatsProvider)
        : base(userManager)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _logger = logger;
        _environment = environment;
        _onboardingService = onboardingService;
        _configRegistry = configRegistry;
        _queryStatistics = queryStatistics;
        _cacheStatsProvider = cacheStatsProvider;
    }

    [HttpGet("")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult Index()
    {
        return View();
    }

    [HttpPost("Humans/{id}/Purge")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PurgeHuman(Guid id)
    {
        if (_environment.IsProduction())
        {
            return NotFound();
        }

        var user = await FindUserByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var currentUser = await GetCurrentUserAsync();

        if (user.Id == currentUser?.Id)
        {
            SetError("You cannot purge your own account.");
            return RedirectToAction(nameof(ProfileController.AdminDetail), "Profile", new { id });
        }

        var displayName = user.DisplayName;

        _logger.LogWarning(
            "Admin {AdminId} purging human {HumanId} ({DisplayName}) in {Environment}",
            currentUser?.Id, id, displayName, _environment.EnvironmentName);

        // Sever OAuth link so next Google login creates a fresh user
        var logins = await _userManager.GetLoginsAsync(user);
        foreach (var login in logins)
        {
            await _userManager.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
        }

        var result = await _onboardingService.PurgeHumanAsync(id);
        if (!result.Success)
        {
            return NotFound();
        }

        SetSuccess($"Purged {displayName}. They will get a fresh account on next login.");
        return RedirectToAction(nameof(ProfileController.AdminList), "Profile");
    }

    [HttpGet("Logs")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult Logs(int count = 200)
    {
        count = Math.Clamp(count, 1, 200);
        var sink = Infrastructure.InMemoryLogSink.Instance;
        var events = sink.GetEvents(count);
        ViewBag.LifetimeCounts = sink.GetLifetimeCounts();
        return View(events);
    }

    [HttpGet("Configuration")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult Configuration()
    {
        var entries = _configRegistry.GetAll();

        var items = entries.Select(e =>
        {
            string? displayValue;
            if (!e.IsSet)
            {
                displayValue = "(not set)";
            }
            else if (e.IsSensitive)
            {
                // Show first 4 chars so you can tell which key is in use;
                // fully mask only very short values (≤4 chars) where the prefix IS the secret
                displayValue = e.Value switch
                {
                    { Length: > 4 } v => v[..4] + "••••••",
                    not null => "••••••",
                    _ => "••••••"
                };
            }
            else
            {
                displayValue = e.Value ?? "(set)";
            }

            return new ConfigurationItemViewModel
            {
                Section = e.Section,
                Key = e.Key,
                IsSet = e.IsSet,
                DisplayValue = displayValue,
                IsSensitive = e.IsSensitive,
                Importance = e.Importance switch
                {
                    ConfigurationImportance.Critical => "critical",
                    ConfigurationImportance.Recommended => "recommended",
                    _ => "optional"
                },
            };
        }).ToList();

        return View(new AdminConfigurationViewModel { Items = items });
    }

    // Intentionally anonymous: exposes only migration names and counts (no sensitive data).
    // Used by dev tooling to check which migrations have been applied in QA/prod,
    // so old migrations can be safely squashed and removed from the repo.
    [HttpGet("DbVersion")]
    [AllowAnonymous]
    [Produces("application/json")]
    public async Task<IActionResult> DbVersion(CancellationToken ct)
    {
        var applied = (await _dbContext.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var pending = await _dbContext.Database.GetPendingMigrationsAsync(ct);

        return Ok(new
        {
            lastApplied = applied.LastOrDefault(),
            appliedCount = applied.Count,
            pendingCount = pending.Count()
        });
    }

    [HttpGet("DbStats")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult DbStats()
    {
        try
        {
            var snapshot = _queryStatistics.GetSnapshot();
            var model = new DbStatsViewModel
            {
                TotalQueryCount = _queryStatistics.TotalCount,
                Entries = snapshot.Select(e => new DbStatEntryViewModel
                {
                    Operation = e.Operation,
                    Table = e.Table,
                    Count = e.Count,
                    AverageMs = Math.Round(e.AverageMilliseconds, 2),
                    MaxMs = Math.Round(e.MaxMilliseconds, 2),
                    TotalMs = Math.Round(e.TotalMilliseconds, 2)
                }).ToList()
            };
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading DB stats");
            SetError("Failed to load database statistics.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("DbStats/Reset")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public IActionResult ResetDbStats()
    {
        try
        {
            _queryStatistics.Reset();
            _logger.LogInformation("Admin reset DB query statistics");
            SetSuccess("Query statistics have been reset.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting DB stats");
            SetError("Failed to reset database statistics.");
        }

        return RedirectToAction(nameof(DbStats));
    }

    [HttpPost("ClearHangfireLocks")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearHangfireLocks()
    {
        var deleted = await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM hangfire.lock");

        _logger.LogWarning("Admin cleared {Count} stale Hangfire locks", deleted);
        SetSuccess($"Cleared {deleted} Hangfire lock(s). Restart the app to re-register recurring jobs.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("CacheStats")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult CacheStats()
    {
        try
        {
            var snapshot = _cacheStatsProvider.GetSnapshot();
            var entryCounts = (_cacheStatsProvider as Humans.Infrastructure.Services.TrackingMemoryCache)
                ?.GetActiveEntryCounts()
                ?? new Dictionary<string, int>(StringComparer.Ordinal);

            var model = new CacheStatsViewModel
            {
                TotalHits = _cacheStatsProvider.TotalHits,
                TotalMisses = _cacheStatsProvider.TotalMisses,
                TotalActiveEntries = _cacheStatsProvider.TotalActiveEntries,
                Entries = snapshot.Select(e =>
                {
                    entryCounts.TryGetValue(e.KeyType, out var activeCount);
                    Application.CacheKeys.Metadata.TryGetValue(e.KeyType, out var meta);
                    return new CacheStatEntryViewModel
                    {
                        KeyType = e.KeyType,
                        Hits = e.Hits,
                        Misses = e.Misses,
                        HitRatePercent = e.HitRatePercent,
                        ActiveEntries = activeCount,
                        Ttl = meta?.Ttl ?? "—",
                        Type = meta?.Type.ToString() ?? "—"
                    };
                }).ToList()
            };
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading cache stats");
            SetError("Failed to load cache statistics.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("CacheStats/Reset")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public IActionResult ResetCacheStats()
    {
        try
        {
            _cacheStatsProvider.Reset();
            _logger.LogInformation("Admin reset cache statistics");
            SetSuccess("Cache statistics have been reset.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting cache stats");
            SetError("Failed to reset cache statistics.");
        }

        return RedirectToAction(nameof(CacheStats));
    }

    [HttpGet("Audience")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> AudienceSegmentation(int? year)
    {
        try
        {
            var allUserIds = await _dbContext.Users
                .Select(u => u.Id)
                .ToListAsync();

            var profileUserIds = await _dbContext.Profiles
                .Select(p => p.UserId)
                .ToHashSetAsync();

            // Get user IDs with tickets, optionally filtered by year
            HashSet<Guid> ticketUserIds;
            if (year.HasValue)
            {
                var yearStart = new DateTime(year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var yearEnd = new DateTime(year.Value + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                var orderUserIds = await _dbContext.TicketOrders
                    .Where(o => o.MatchedUserId != null &&
                                o.PurchasedAt >= NodaTime.Instant.FromDateTimeUtc(yearStart) &&
                                o.PurchasedAt < NodaTime.Instant.FromDateTimeUtc(yearEnd))
                    .Select(o => o.MatchedUserId!.Value)
                    .Distinct()
                    .ToListAsync();

                var attendeeUserIds = await _dbContext.TicketAttendees
                    .Where(a => a.MatchedUserId != null &&
                                a.TicketOrder.PurchasedAt >= NodaTime.Instant.FromDateTimeUtc(yearStart) &&
                                a.TicketOrder.PurchasedAt < NodaTime.Instant.FromDateTimeUtc(yearEnd))
                    .Select(a => a.MatchedUserId!.Value)
                    .Distinct()
                    .ToListAsync();

                ticketUserIds = orderUserIds.Concat(attendeeUserIds).ToHashSet();
            }
            else
            {
                var orderUserIds = await _dbContext.TicketOrders
                    .Where(o => o.MatchedUserId != null)
                    .Select(o => o.MatchedUserId!.Value)
                    .Distinct()
                    .ToListAsync();

                var attendeeUserIds = await _dbContext.TicketAttendees
                    .Where(a => a.MatchedUserId != null)
                    .Select(a => a.MatchedUserId!.Value)
                    .Distinct()
                    .ToListAsync();

                ticketUserIds = orderUserIds.Concat(attendeeUserIds).ToHashSet();
            }

            var totalAccounts = allUserIds.Count;
            var withProfile = 0;
            var withTicket = 0;
            var withBoth = 0;
            var withNeither = 0;

            foreach (var userId in allUserIds)
            {
                var hasProfile = profileUserIds.Contains(userId);
                var hasTicket = ticketUserIds.Contains(userId);

                if (hasProfile) withProfile++;
                if (hasTicket) withTicket++;
                if (hasProfile && hasTicket) withBoth++;
                if (!hasProfile && !hasTicket) withNeither++;
            }

            // Get available years from ticket orders
            var availableYears = await _dbContext.TicketOrders
                .Where(o => o.MatchedUserId != null)
                .Select(o => o.PurchasedAt)
                .Distinct()
                .ToListAsync();

            var years = availableYears
                .Select(i => i.ToDateTimeUtc().Year)
                .Distinct()
                .OrderDescending()
                .ToList();

            var model = new AudienceSegmentationViewModel
            {
                TotalAccounts = totalAccounts,
                WithTicket = withTicket,
                WithProfile = withProfile,
                WithBoth = withBoth,
                WithNeither = withNeither,
                AvailableYears = years,
                SelectedYear = year,
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading audience segmentation data");
            SetError("Failed to load audience segmentation data.");
            return RedirectToAction(nameof(Index));
        }
    }
}
