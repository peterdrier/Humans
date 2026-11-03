namespace Humans.Camps.Services;

/// <summary>
/// Covers all active definitions (not just <c>MinimumRequired &gt; 0</c>) and uses <see cref="SlotCount"/> as the
/// denominator.
/// </summary>
internal sealed record CampDirectoryRoleSummary(
    string Name,
    int Filled,
    int SlotCount)
{
    public int SortOrder { get; init; }
}
