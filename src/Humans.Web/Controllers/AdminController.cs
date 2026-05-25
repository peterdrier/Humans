using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Feedback;
using Humans.Application.Interfaces.Shifts;
using Humans.Infrastructure.Data;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Route("Admin")]
public class AdminController(
    IUserServiceRead userService,
    ILogger<AdminController> logger,
    IWebHostEnvironment environment,
    IAccountDeletionService accountDeletionService,
    ConfigurationRegistry configRegistry,
    QueryStatistics queryStatistics,
    ICacheStatsProvider cacheStatsProvider,
    IEnumerable<ICacheStats> decoratorCacheStats,
    IUserEmailProviderBackfillService userEmailProviderBackfillService,
    IAdminDatabaseDiagnosticsService databaseDiagnostics) : HumansControllerBase(userService)
{
    // AnyAdminRole so top-nav doesn't 403 for FinanceAdmin etc.; tiles are aggregate counts safe across roles. Other actions stay AdminOnly.
    [HttpGet("")]
    [Authorize(Policy = PolicyNames.AnyAdminRole)]
    public async Task<IActionResult> Index(
        [FromServices] IShiftManagementService shifts,
        [FromServices] IFeedbackService feedback,
        [FromServices] IAuditViewerService auditViewer,
        [FromServices] IAdminDashboardService adminDashboardService,
        [FromServices] IUserServiceRead userService,
        [FromServices] IUserActivityTracker activityTracker,
        CancellationToken ct)
    {
        var firstName = User.Identity?.Name?.Split(' ').FirstOrDefault() ?? "";
        var snapshot = await userService.GetAllUserInfosAsync(ct);
        var totalUsers = snapshot.Count;
        var activeProfileUsers = snapshot.Count(u => u.IsActive);
        var activeEvent = await shifts.GetActiveAsync();
        var ticketHolders = activeEvent is { Year: > 0 }
            ? snapshot.Count(u => u.HasTicketForYear(activeEvent.Year))
            : 0;
        var (filled, total, ratio) = await shifts.GetOverallCoverageAsync(ct);
        var openFeedback = await feedback.GetActionableCountAsync(ct);
        var recent = (await auditViewer.GetRecentAsync(8, ct))
            .Select(e => new DashboardActivityRow(e.Action, e.Description, e.OccurredAt))
            .ToArray();
        var staffing = Array.Empty<DepartmentCoverage>();

        var dashboardData = await adminDashboardService.GetAdminDashboardAsync(ct);
        var appStats = new DashboardApplicationStats(
            Total: dashboardData.TotalApplications,
            Approved: dashboardData.ApprovedApplications,
            Rejected: dashboardData.RejectedApplications,
            Colaborador: dashboardData.ColaboradorApplied,
            Asociado: dashboardData.AsociadoApplied);
        var languages = dashboardData.LanguageDistribution
            .Select(l => new DashboardLanguageCount(l.Language, l.Count))
            .ToArray();

        var vm = new AdminDashboardViewModel(
            GreetingFirstName: firstName,
            TotalUsers: totalUsers,
            ActiveProfileUsers: activeProfileUsers,
            TicketHolders: ticketHolders,
            ShiftCoveragePercent: total > 0 ? (int)Math.Round(ratio * 100) : 0,
            ShiftFilledOf: total > 0 ? filled : null,
            ShiftTotalOf: total > 0 ? total : null,
            OpenFeedback: openFeedback,
            OnlineNow: activityTracker.CountActiveWithin(Duration.FromMinutes(5)),
            OnlineLastHour: activityTracker.CountActiveWithin(Duration.FromHours(1)),
            OnlineLast24h: activityTracker.CountActiveWithin(Duration.FromHours(24)),
            StaffingByDepartment: staffing,
            RecentActivity: recent,
            AppStats: appStats,
            LanguageDistribution: languages,
            SetMembership: dashboardData.SetMembership);
        return View(vm);
    }

    [HttpPost("Humans/{id}/Purge")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PurgeHuman(Guid id)
    {
        if (environment.IsProduction())
        {
            return NotFound();
        }

        var user = await FindUserInfoByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var currentUser = await GetCurrentUserInfoAsync();

        if (user.Id == currentUser?.Id)
        {
            SetError("You cannot purge your own account.");
            return RedirectToAction(nameof(ProfileController.AdminDetail), "Profile", new { id });
        }

        var displayName = user.BurnerName;

        logger.LogWarning(
            "Admin {AdminId} purging human {HumanId} ({DisplayName}) in {Environment}",
            currentUser?.Id, id, displayName, environment.EnvironmentName);

        var result = await accountDeletionService.PurgeAsync(id, currentUser?.Id);
        if (!result.Success)
        {
            return NotFound();
        }

        SetSuccess($"Purged {displayName}. They will get a fresh account on next login.");
        return RedirectToAction(nameof(ProfileController.AdminList), "Profile");
    }

    [HttpGet("Logs")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult Logs(int count = 1000)
    {
        count = Math.Clamp(count, 1, 1000);
        var sink = Infrastructure.InMemoryLogSink.Instance;
        var events = sink.GetEvents(count);
        ViewBag.LifetimeCounts = sink.GetLifetimeCounts();
        ViewBag.SinkStartedAt = sink.StartedAt;
        ViewBag.TotalEvents = sink.TotalEvents;
        return View(events);
    }

    [HttpGet("Maintenance")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult Maintenance() => View();

    [HttpGet("Configuration")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult Configuration()
    {
        var entries = configRegistry.GetAll();

        var items = entries.Select(e =>
        {
            string? displayValue;
            if (!e.IsSet)
            {
                displayValue = "(not set)";
            }
            else if (e.IsSensitive)
            {
                // First 4 chars to identify key; fully mask ≤4-char values (prefix would be the secret).
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

    // Anonymous on purpose: only migration names + counts (no sensitive data). Dev tooling checks QA/prod state before squashing migrations.
    [HttpGet("DbVersion")]
    [AllowAnonymous]
    [Produces("application/json")]
    public async Task<IActionResult> DbVersion(CancellationToken ct)
    {
        var status = await databaseDiagnostics.GetMigrationStatusAsync(ct);

        return Ok(new
        {
            lastApplied = status.LastApplied,
            appliedCount = status.AppliedCount,
            pendingCount = status.PendingCount,
            recentApplied = status.Applied.TakeLast(20).Reverse().ToList()
        });
    }

    [HttpGet("DbStats")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult DbStats()
    {
        try
        {
            var snapshot = queryStatistics.GetSnapshot();
            var model = new DbStatsViewModel
            {
                TotalQueryCount = queryStatistics.TotalCount,
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
            logger.LogError(ex, "Error loading DB stats");
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
            queryStatistics.Reset();
            logger.LogInformation("Admin reset DB query statistics");
            SetSuccess("Query statistics have been reset.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error resetting DB stats");
            SetError("Failed to reset database statistics.");
        }

        return RedirectToAction(nameof(DbStats));
    }

    [HttpPost("ClearHangfireLocks")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearHangfireLocks(CancellationToken ct)
    {
        var deleted = await databaseDiagnostics.ClearHangfireLocksAsync(ct);

        logger.LogWarning("Admin cleared {Count} stale Hangfire locks", deleted);
        SetSuccess($"Cleared {deleted} Hangfire lock(s). Restart the app to re-register recurring jobs.");
        return RedirectToAction(nameof(Maintenance));
    }

    /// <summary>
    /// One-shot, idempotent backfill of UserEmail.Provider/ProviderKey/IsGoogle from AspNetUserLogins and legacy User.GoogleEmail.
    /// PR 3 of email-identity-decoupling spec; legacy columns dropped in PR 7.
    /// </summary>
    [HttpGet("BackfillUserEmailProviders")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult BackfillUserEmailProviders()
    {
        return View(new BackfillUserEmailProvidersViewModel(
            HasRun: false,
            UsersProcessed: 0,
            ProviderRowsUpdated: 0,
            IsGoogleRowsUpdated: 0,
            AmbiguousMatchesWarned: 0,
            Warnings: []));
    }

    [HttpPost("BackfillUserEmailProviders")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BackfillUserEmailProvidersRun(CancellationToken ct)
    {
        var currentUser = await GetCurrentUserInfoAsync();
        logger.LogInformation(
            "Admin {AdminId} running UserEmail Provider/IsGoogle backfill",
            currentUser?.Id);

        var result = await userEmailProviderBackfillService.RunAsync(ct);

        var msg =
            $"Provider/IsGoogle backfill complete. Users processed: {result.UsersProcessed}. " +
            $"Provider rows updated: {result.ProviderRowsUpdated}. " +
            $"IsGoogle rows updated: {result.IsGoogleRowsUpdated}.";
        if (result.AmbiguousMatchesWarned > 0)
            msg += $" {result.AmbiguousMatchesWarned} user(s) with ambiguous AspNetUserLogins matches — see view for details.";
        SetSuccess(msg);

        return View(nameof(BackfillUserEmailProviders), new BackfillUserEmailProvidersViewModel(
            HasRun: true,
            UsersProcessed: result.UsersProcessed,
            ProviderRowsUpdated: result.ProviderRowsUpdated,
            IsGoogleRowsUpdated: result.IsGoogleRowsUpdated,
            AmbiguousMatchesWarned: result.AmbiguousMatchesWarned,
            Warnings: result.Warnings));
    }

    [HttpGet("CacheStats")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public IActionResult CacheStats()
    {
        try
        {
            var snapshot = cacheStatsProvider.GetSnapshot();
            var entryCounts = (cacheStatsProvider as Humans.Infrastructure.Services.TrackingMemoryCache)
                ?.GetActiveEntryCounts()
                ?? new Dictionary<string, int>(StringComparer.Ordinal);

            var model = new CacheStatsViewModel
            {
                TotalHits = cacheStatsProvider.TotalHits,
                TotalMisses = cacheStatsProvider.TotalMisses,
                TotalActiveEntries = cacheStatsProvider.TotalActiveEntries,
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
                }).ToList(),
                DecoratorEntries = decoratorCacheStats
                    .OrderBy(s => s.Name, StringComparer.Ordinal)
                    .Select(s => new DecoratorCacheStatEntryViewModel
                    {
                        Name = s.Name,
                        Entries = s.Entries,
                        Hits = s.Hits,
                        Misses = s.Misses,
                        KeyRemovals = s.KeyRemovals,
                        BulkInvalidations = s.BulkInvalidations,
                        HitRatePercent = s.HitRatePercent,
                        IsWarmedUp = s.IsWarmedUp,
                    })
                    .ToList()
            };
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading cache stats");
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
            cacheStatsProvider.Reset();
            foreach (var s in decoratorCacheStats)
                s.ResetCounters();
            logger.LogInformation("Admin reset cache statistics");
            SetSuccess("Cache statistics have been reset.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error resetting cache stats");
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
            var segmentation = await databaseDiagnostics.GetAudienceSegmentationAsync(year);

            var model = new AudienceSegmentationViewModel
            {
                TotalAccounts = segmentation.TotalAccounts,
                WithTicket = segmentation.WithTicket,
                WithProfile = segmentation.WithProfile,
                WithBoth = segmentation.WithBoth,
                WithNeither = segmentation.WithNeither,
                AvailableYears = segmentation.AvailableYears.ToList(),
                SelectedYear = segmentation.SelectedYear,
            };

            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading audience segmentation data");
            SetError("Failed to load audience segmentation data.");
            return RedirectToAction(nameof(Index));
        }
    }
}
