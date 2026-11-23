using Humans.Base.Attributes;

namespace Humans.Base.Interfaces;

/// <summary>
/// One match on the global <c>/Search</c> page, pre-scored by its owning section. The category's component renders
/// the row from these properties, bound by name: it takes <c>Guid key</c> and may take the match context.
/// </summary>
/// <param name="Key">The matched entity's id.</param>
/// <param name="SortKey">The tiebreak after <paramref name="Score"/>; never rendered.</param>
/// <param name="Score">Higher is better.</param>
/// <param name="MatchField">The localized label of the field that matched, when the row shows it.</param>
/// <param name="MatchSnippet">A long-form excerpt around the match, when there is one.</param>
public sealed record SearchHit(Guid Key, string SortKey, int Score, string? MatchField = null, string? MatchSnippet = null);

/// <summary>One result category (filter chip, heading and bucket) on the global <c>/Search</c> page.</summary>
/// <param name="Key">The stable <c>filter</c> query value.</param>
/// <param name="Label">The localized chip and heading text, resolved from the contributing section's own resources.</param>
/// <param name="IconCssClass">The chip and heading icon.</param>
/// <param name="ResultComponent">The section's view component for one <see cref="SearchHit"/> row.</param>
/// <param name="SearchAsync">
/// Every match for an already-trimmed query of at least two characters, unsorted and uncapped. Text matches see
/// only the section's public surface; a pasted entity id may resolve past it, and the destination page decides
/// whether the viewer may open it.
/// </param>
/// <param name="Weight">Chip and bucket order, ascending.</param>
/// <param name="IsEnabled">Null means always on; false hides the category end to end and skips its search.</param>
public sealed record SearchCategory(
    string Key,
    Func<IServiceProvider, string> Label,
    string IconCssClass,
    Type ResultComponent,
    Func<IServiceProvider, string, CancellationToken, Task<IReadOnlyList<SearchHit>>> SearchAsync,
    int Weight = 0,
    Func<IServiceProvider, bool>? IsEnabled = null);

/// <summary>The categories a section contributes to the global <c>/Search</c> page.</summary>
[ViewComponentSlot(typeof(SearchHit))]
public interface ISearchContributor : ISectionContribution
{
    IEnumerable<SearchCategory> Categories();
}
