namespace Humans.Base.Interfaces;

/// <summary>One tab a section contributes to the member-facing /Settings page.</summary>
/// <remarks>
/// <paramref name="Key"/> is the URL fragment and the merge identity — two sections
/// contributing the same <paramref name="Key"/> collapse to one tab, first contributor wins.
/// <paramref name="Label"/> is a SharedResource key; a key with no entry renders as itself.
/// <paramref name="ComponentName"/> names a view component the owning section ships; it
/// renders the tab's panel body. <paramref name="Policy"/> null means every authenticated
/// user sees the tab — the tab decides for itself whether that user gets a read-only or
/// editable view. <paramref name="Weight"/> orders tabs; sorting is stable, so equal weights
/// keep discovery order.
/// </remarks>
public sealed record SettingsTab(
    string Key,
    string Label,
    string ComponentName,
    string? Policy = null,
    int Weight = 0);

/// <summary>The /Settings tabs a section contributes.</summary>
public interface ISectionSettings : ISectionContribution
{
    IEnumerable<SettingsTab> Tabs();
}
