namespace Humans.Application.Services.Camps;

/// <summary>
/// Per-season fill count for a single role definition, used by the /Barrios
/// directory "show lead positions" pills. Covers ALL active definitions (not just
/// <c>MinimumRequired &gt; 0</c>) and uses <see cref="SlotCount"/> as the denominator.
/// </summary>
public sealed record CampDirectoryRoleSummary(
    string Name,
    int Filled,
    int SlotCount);
