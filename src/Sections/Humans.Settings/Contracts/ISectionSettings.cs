using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Settings.Contracts;

/// <summary>
/// <paramref name="Key"/> is the URL fragment and the merge identity: a duplicate key collapses to one tab, first
/// contributor wins. <paramref name="Label"/> is a SharedResource key, because <c>SettingsTabsViewComponent</c>
/// renders every label and cannot see a contributor's own resource set; a key with no entry renders as itself. A null
/// <paramref name="Policy"/> shows the tab to every signed-in member, and the tab decides read-only vs editable.
/// Equal <paramref name="Weight"/>s keep discovery order.
/// </summary>
public sealed record SettingsTab(
    string Key,
    string Label,
    Type Component,
    string? Policy = null,
    int Weight = 0);

[ViewComponentSlot]
public interface ISectionSettings : ISectionContribution
{
    IEnumerable<SettingsTab> Tabs();
}
