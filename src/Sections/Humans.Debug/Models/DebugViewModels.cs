namespace Humans.Debug.Models;

/// <summary>The value is already masked for display.</summary>
internal sealed class ConfigurationItemViewModel
{
    public string Section { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public bool IsSet { get; set; }
    public string? DisplayValue { get; set; }
    public bool IsSensitive { get; set; }
    public string Importance { get; set; } = "optional";
}

internal sealed class AdminConfigurationViewModel
{
    public List<ConfigurationItemViewModel> Items { get; set; } = [];
}

internal sealed class DbStatsViewModel
{
    public long TotalQueryCount { get; set; }
    public List<DbStatEntryViewModel> Entries { get; set; } = [];
}

internal sealed class DbStatEntryViewModel
{
    public string Operation { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public long Count { get; set; }
    public double AverageMs { get; set; }
    public double MaxMs { get; set; }
    public double TotalMs { get; set; }
}

internal sealed class CacheStatsViewModel
{
    public long TotalHits { get; set; }
    public long TotalMisses { get; set; }
    public int TotalActiveEntries { get; set; }

    public double OverallHitRatePercent => TotalHits + TotalMisses > 0
        ? Math.Round(TotalHits * 100.0 / (TotalHits + TotalMisses), 1)
        : 0;

    public List<CacheStatEntryViewModel> Entries { get; set; } = [];

    /// <summary>TrackedCache decorators have no TTL, so they track invalidation count instead.</summary>
    public List<DecoratorCacheStatEntryViewModel> DecoratorEntries { get; set; } = [];
}

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

internal sealed class TimingsViewModel
{
    public List<TimingEntryViewModel> Entries { get; set; } = [];
    public List<SwallowedEntryViewModel> Swallowed { get; set; } = [];
}

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

internal sealed class SwallowedEntryViewModel
{
    public string Operation { get; set; } = string.Empty;
    public long Count { get; set; }
}
