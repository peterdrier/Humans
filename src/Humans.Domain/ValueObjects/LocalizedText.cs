namespace Humans.Domain.ValueObjects;

/// <summary>Per-culture authored content (culture code → text). Persisted as a single jsonb column.</summary>
public sealed class LocalizedText : IEquatable<LocalizedText>
{
    private readonly Dictionary<string, string> _values;

    public LocalizedText(IDictionary<string, string> values)
        => _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

    public static LocalizedText Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Requested culture → default culture → any present → "".</summary>
    public string Resolve(string culture, string defaultCulture)
    {
        if (_values.TryGetValue(culture, out var v) && !string.IsNullOrEmpty(v)) return v;
        if (_values.TryGetValue(defaultCulture, out var d) && !string.IsNullOrEmpty(d)) return d;
        foreach (var s in _values.Values) if (!string.IsNullOrEmpty(s)) return s;
        return string.Empty;
    }

    public bool HasCulture(string culture) => _values.TryGetValue(culture, out var v) && !string.IsNullOrEmpty(v);

    public bool Equals(LocalizedText? other) =>
        other is not null && _values.Count == other._values.Count &&
        _values.All(kv => other._values.TryGetValue(kv.Key, out var o) && string.Equals(o, kv.Value, StringComparison.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as LocalizedText);

    public override int GetHashCode() =>
        _values.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
               .Aggregate(0, (h, kv) => HashCode.Combine(
                   h,
                   StringComparer.OrdinalIgnoreCase.GetHashCode(kv.Key),
                   StringComparer.Ordinal.GetHashCode(kv.Value)));
}
