namespace Humans.Governance.Domain;

/// <summary>
/// Per-culture authored content (culture code → text), persisted as a single jsonb column.
/// </summary>
/// <remarks>
/// Governance owns its own copy rather than sharing Surveys' <c>LocalizedText</c> or promoting
/// one to Base (spec decision 5, 2026-09-10): the two sections' authored content has no shared
/// contract, and a Base type would be public surface nothing outside these sections needs.
/// </remarks>
internal sealed class GovernanceLocalizedText : IEquatable<GovernanceLocalizedText>
{
    private readonly Dictionary<string, string> _values;

    public GovernanceLocalizedText(IDictionary<string, string> values)
        => _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

    public static GovernanceLocalizedText Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Requested culture → official culture → any present → "".</summary>
    public string Resolve(string culture, string officialCulture)
    {
        if (_values.TryGetValue(culture, out var v) && !string.IsNullOrEmpty(v)) return v;
        if (_values.TryGetValue(officialCulture, out var d) && !string.IsNullOrEmpty(d)) return d;
        foreach (var s in _values.Values) if (!string.IsNullOrEmpty(s)) return s;
        return string.Empty;
    }

    /// <summary>True when the requested culture carries its own text (i.e. is not a fallback).</summary>
    public bool HasCulture(string culture) =>
        _values.TryGetValue(culture, out var v) && !string.IsNullOrEmpty(v);

    public bool Equals(GovernanceLocalizedText? other) =>
        other is not null && _values.Count == other._values.Count &&
        _values.All(kv => other._values.TryGetValue(kv.Key, out var o) && string.Equals(o, kv.Value, StringComparison.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as GovernanceLocalizedText);

    public override int GetHashCode() =>
        _values.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
               .Aggregate(0, (h, kv) => HashCode.Combine(
                   h,
                   StringComparer.OrdinalIgnoreCase.GetHashCode(kv.Key),
                   StringComparer.Ordinal.GetHashCode(kv.Value)));
}
