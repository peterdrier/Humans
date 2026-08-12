namespace Humans.Debug.Models;

// Moved out of Humans.Web/Models/AdminViewModels.cs at the section's G5
// (nobodies-collective/Humans#866). Every type here had exactly one production consumer —
// DebugController and its views — so the whole block came with the section and turned
// internal (step 5). Nothing outside Humans.Debug ever named one.

/// <summary>One auto-discovered configuration entry, with its value already masked for display.</summary>
internal sealed class ConfigurationItemViewModel
{
    public string Section { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public bool IsSet { get; set; }
    public string? DisplayValue { get; set; }
    public bool IsSensitive { get; set; }
    public string Importance { get; set; } = "optional";
}

/// <summary>View model for <c>/Debug/Configuration</c>.</summary>
internal sealed class AdminConfigurationViewModel
{
    public List<ConfigurationItemViewModel> Items { get; set; } = [];
}

/// <summary>View model for <c>/Debug/DbStats</c>.</summary>
internal sealed class DbStatsViewModel
{
    public long TotalQueryCount { get; set; }
    public List<DbStatEntryViewModel> Entries { get; set; } = [];
}

/// <summary>One operation/table pair with its query count and timings.</summary>
internal sealed class DbStatEntryViewModel
{
    public string Operation { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public long Count { get; set; }
    public double AverageMs { get; set; }
    public double MaxMs { get; set; }
    public double TotalMs { get; set; }
}

/// <summary>View model for <c>/Debug/CacheStats</c>.</summary>
internal sealed class CacheStatsViewModel
{
    public long TotalHits { get; set; }
    public long TotalMisses { get; set; }
    public int TotalActiveEntries { get; set; }

    public double OverallHitRatePercent => TotalHits + TotalMisses > 0
        ? Math.Round(TotalHits * 100.0 / (TotalHits + TotalMisses), 1)
        : 0;

    public List<CacheStatEntryViewModel> Entries { get; set; } = [];

    /// <summary>
    /// Stats for the in-memory caching decorators (Profile / User / Team /
    /// ShiftView), rendered in a separate table below the IMemoryCache stats.
    /// These caches have no TTL and track invalidation count instead.
    /// </summary>
    public List<DecoratorCacheStatEntryViewModel> DecoratorEntries { get; set; } = [];
}

/// <summary>One <c>IMemoryCache</c> key type with its hit/miss counters and TTL metadata.</summary>
internal sealed class CacheStatEntryViewModel
{
    public string KeyType { get; set; } = string.Empty;
    public long Hits { get; set; }
    public long Misses { get; set; }
    public double HitRatePercent { get; set; }
    public int ActiveEntries { get; set; }
    public string Ttl { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

/// <summary>One §15 caching decorator with its counters and warm-up state.</summary>
internal sealed class DecoratorCacheStatEntryViewModel
{
    public string Name { get; set; } = string.Empty;
    public int Entries { get; set; }
    public long Hits { get; set; }
    public long Misses { get; set; }
    public long KeyRemovals { get; set; }
    public long BulkInvalidations { get; set; }
    public double HitRatePercent { get; set; }
    public bool IsWarmedUp { get; set; }
}

/// <summary>View model for <c>/Debug/Timings</c>.</summary>
internal sealed class TimingsViewModel
{
    public List<TimingEntryViewModel> Entries { get; set; } = [];
    public List<SwallowedEntryViewModel> Swallowed { get; set; } = [];
}

/// <summary>One timed operation: call count, last/avg/min/max/total ms, last-called stamp.</summary>
internal sealed class TimingEntryViewModel
{
    public string Operation { get; set; } = string.Empty;
    public long Count { get; set; }
    public double LastMs { get; set; }
    public double AvgMs { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public double TotalMs { get; set; }
    public DateTime LastAtUtc { get; set; }
}

/// <summary>One operation whose exceptions were swallowed, with how often.</summary>
internal sealed class SwallowedEntryViewModel
{
    public string Operation { get; set; } = string.Empty;
    public long Count { get; set; }
}
