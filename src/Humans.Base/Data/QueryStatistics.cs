using System.Collections.Concurrent;

namespace Humans.Base.Data;

/// <summary>Thread-safe in-memory DB query statistics; a singleton, so they reset on restart.</summary>
public class QueryStatistics
{
    private readonly ConcurrentDictionary<string, QueryStatEntry> _entries = new(StringComparer.Ordinal);

    public void Record(string operation, string table, double elapsedMilliseconds)
    {
        var key = $"{operation}:{table}";
        _entries.AddOrUpdate(
            key,
            _ => new QueryStatEntry(operation, table, 1, elapsedMilliseconds, elapsedMilliseconds),
            (_, existing) => existing.Add(elapsedMilliseconds));
    }

    /// <summary>Ordered by count, descending.</summary>
    public IReadOnlyList<QueryStatEntry> GetSnapshot()
    {
        return _entries.Values
            .OrderByDescending(e => e.Count)
            .ToList();
    }

    public void Reset()
    {
        _entries.Clear();
    }

    public long TotalCount => _entries.Values.Sum(e => e.Count);
}

public sealed class QueryStatEntry(string operation, string table, long count, double totalMs, double maxMs)
{
    public string Operation { get; } = operation;
    public string Table { get; } = table;
    public long Count { get; private set; } = count;
    public double TotalMilliseconds { get; private set; } = totalMs;
    public double MaxMilliseconds { get; private set; } = maxMs;

    public double AverageMilliseconds => Count > 0 ? TotalMilliseconds / Count : 0;

    internal QueryStatEntry Add(double elapsedMs)
    {
        // ConcurrentDictionary.AddOrUpdate serializes calls per key, so this is safe
        Count++;
        TotalMilliseconds += elapsedMs;
        if (elapsedMs > MaxMilliseconds)
            MaxMilliseconds = elapsedMs;
        return this;
    }
}
