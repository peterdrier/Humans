using Humans.Application.Configuration;
using Humans.Application.Diagnostics;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Data;
using Humans.Web.Authorization;
using Humans.Web.Infrastructure;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog.Events;

namespace Humans.Web.Controllers;

/// <summary>
/// Developer / diagnostics pages. The whole section is admin-gated, so pages
/// live at <c>/Debug/*</c> directly.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Debug")]
public class DebugController(
    IUserServiceRead userService,
    IClientStatsTracker clientStats,
    IHttpStatusTracker httpStatus,
    ILogger<DebugController> logger,
    ConfigurationRegistry configRegistry,
    QueryStatistics queryStatistics,
    ICacheStatsProvider cacheStatsProvider,
    IEnumerable<ICacheStats> decoratorCacheStats,
    IAdminDatabaseDiagnosticsService databaseDiagnostics) : HumansControllerBase(userService)
{
    [HttpGet("Logs")]
    public IActionResult Logs(int count = 1000, string? minLevel = null)
    {
        count = Math.Clamp(count, 1, 1000);

        LogEventLevel? minLogLevel = minLevel?.ToUpperInvariant() switch
        {
            "WARNING" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "FATAL" => LogEventLevel.Fatal,
            _ => null
        };

        var sink = InMemoryLogSink.Instance;
        var events = sink.GetEvents(count, minLogLevel);
        ViewBag.LifetimeCounts = sink.GetLifetimeCounts();
        ViewBag.SinkStartedAt = sink.StartedAt;
        ViewBag.TotalEvents = sink.TotalEvents;
        ViewBag.MinLevel = minLevel;
        return View(events);
    }

    [HttpGet("Maintenance")]
    public IActionResult Maintenance() => View();

    [HttpPost("Maintenance/ClearHangfireLocks")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearHangfireLocks(CancellationToken ct)
    {
        var deleted = await databaseDiagnostics.ClearHangfireLocksAsync(ct);

        logger.LogWarning("Admin cleared {Count} stale Hangfire locks", deleted);
        SetSuccess($"Cleared {deleted} Hangfire lock(s). Restart the app to re-register recurring jobs.");
        return RedirectToAction(nameof(Maintenance));
    }

    [HttpGet("Configuration")]
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
                // First 4 chars identify the key; fully mask <=4-char values.
                displayValue = e.Value switch
                {
                    { Length: > 4 } v => v[..4] + "******",
                    _ => "******"
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

    // Anonymous on purpose: only migration names + counts, no sensitive data.
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
            return RedirectToAction(nameof(Maintenance));
        }
    }

    [HttpPost("DbStats/Reset")]
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

    [HttpGet("CacheStats")]
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
                    Humans.Application.CacheKeys.Metadata.TryGetValue(e.KeyType, out var meta);
                    return new CacheStatEntryViewModel
                    {
                        KeyType = e.KeyType,
                        Hits = e.Hits,
                        Misses = e.Misses,
                        HitRatePercent = e.HitRatePercent,
                        ActiveEntries = activeCount,
                        Ttl = meta?.Ttl ?? "-",
                        Type = meta?.Type.ToString() ?? "-"
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
            return RedirectToAction(nameof(Maintenance));
        }
    }

    [HttpPost("CacheStats/Reset")]
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

    [HttpGet("ClientStats")]
    public IActionResult ClientStats()
    {
        var snapshot = clientStats.GetSnapshot();

        static IReadOnlyList<ClientStatRow> ToRows(IReadOnlyList<ClientStatCount> items, long total)
            => items.Select(i => new ClientStatRow(
                    i.Label, i.Count, total > 0 ? Math.Round(i.Count * 100.0 / total, 1) : 0))
                .ToList();

        var statusTotal = httpStatus.Total;
        var statusRows = httpStatus.GetCounts()
            .OrderBy(kv => kv.Key)
            .Select(kv => new HttpStatusRow(
                kv.Key,
                StatusCategory(kv.Key),
                kv.Value,
                statusTotal > 0 ? Math.Round(kv.Value * 100.0 / statusTotal, 1) : 0))
            .ToList();

        var totalBotPageViews = snapshot.Bots.Sum(b => b.Count);

        var vm = new ClientStatsViewModel(
            TotalPageViews: snapshot.TotalPageViews,
            OperatingSystems: ToRows(snapshot.OperatingSystems, snapshot.TotalPageViews),
            Browsers: ToRows(snapshot.Browsers, snapshot.TotalPageViews),
            DeviceTypes: ToRows(snapshot.DeviceTypes, snapshot.TotalPageViews),
            TotalBotPageViews: totalBotPageViews,
            Bots: ToRows(snapshot.Bots, totalBotPageViews),
            TotalResolutionSamples: snapshot.TotalResolutionSamples,
            Resolutions: ToRows(snapshot.Resolutions, snapshot.TotalResolutionSamples),
            TotalResponses: statusTotal,
            StatusCodes: statusRows);

        return View(vm);
    }

    [HttpGet("Timings")]
    public IActionResult Timings()
    {
        var registry = OperationTimingRegistry.Instance;

        var entries = registry.GetTimings()
            .OrderByDescending(t => t.TotalMs)
            .Select(t => new TimingEntryViewModel
            {
                Operation = t.Key,
                Count = t.Count,
                LastMs = Math.Round(t.LastMs, 2),
                AvgMs = Math.Round(t.AvgMs, 2),
                MinMs = Math.Round(t.MinMs, 2),
                MaxMs = Math.Round(t.MaxMs, 2),
                TotalMs = Math.Round(t.TotalMs, 2),
                LastAtUtc = t.LastAt.ToDateTimeUtc(),
            })
            .ToList();

        var swallowed = registry.GetSwallowed()
            .OrderByDescending(s => s.Count)
            .Select(s => new SwallowedEntryViewModel
            {
                Operation = s.Key,
                Count = s.Count,
            })
            .ToList();

        return View(new TimingsViewModel { Entries = entries, Swallowed = swallowed });
    }

    [HttpGet("FormatGallery")]
    public IActionResult FormatGallery() => View(FormatGalleryModelBuilder.Build());

    private static string StatusCategory(int statusCode) => statusCode switch
    {
        >= 200 and < 300 => "Success",
        >= 300 and < 400 => "Redirect",
        >= 400 and < 500 => "Client error",
        >= 500 => "Server error",
        _ => "Other"
    };
}
