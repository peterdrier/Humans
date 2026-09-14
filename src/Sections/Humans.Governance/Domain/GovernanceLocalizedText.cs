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

    /// <summary>
    /// Requested culture → official culture → any present → "".
    /// <para>
    /// Whitespace counts as absent, not as authored. Optional translations are neither
    /// required nor normalized on the draft form, so a stray space in one culture's box would
    /// otherwise be resolved as that culture's text — and a blank motion title, blank official
    /// text or blank email subject on a binding vote is worse than the official-culture
    /// wording the reader would have got instead.
    /// </para>
    /// </summary>
    public string Resolve(string culture, string officialCulture)
    {
        if (_values.TryGetValue(culture, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        if (_values.TryGetValue(officialCulture, out var d) && !string.IsNullOrWhiteSpace(d)) return d;
        foreach (var s in _values.Values) if (!string.IsNullOrWhiteSpace(s)) return s;
        return string.Empty;
    }

    /// <summary>
    /// True when the requested culture carries its own text (i.e. is not a fallback). Same
    /// whitespace rule as <see cref="Resolve"/>, so the two cannot disagree about whether a
    /// culture was authored — the page would otherwise drop the "this is a translation"
    /// notice while showing the official culture's text.
    /// </summary>
    public bool HasCulture(string culture) =>
        _values.TryGetValue(culture, out var v) && !string.IsNullOrWhiteSpace(v);

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
