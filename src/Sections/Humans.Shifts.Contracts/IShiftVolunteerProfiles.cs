namespace Humans.Shifts.Contracts;

/// <summary>
/// The volunteer's own shift profile — skills/quirks/languages and shift-tag
/// preferences — as consumed from outside the section: Users' profile edit
/// page reads and writes it.
/// </summary>
/// <remarks>
/// Carries writes, so it is deliberately not called <c>…Read</c>
/// (Governance's rule: a leaf keeps an honest name rather than a
/// <c>Read</c> suffix over write members). The rest of the section's write
/// surface — rotas, shifts, generation, event settings — has no external
/// caller and stays on the internal <c>IShiftManagementService</c>.
///
/// <para>
/// The read crosses the boundary as <see cref="ShiftVolunteerProfileInfo"/>,
/// never as the <c>VolunteerEventProfile</c> entity.
/// </para>
/// </remarks>
public interface IShiftVolunteerProfiles
{
    /// <summary>
    /// Gets a user's shift profile (Skills / Quirks / Languages), or
    /// <c>null</c> when the user has none. Dietary and medical data moved to
    /// Profile — read those via <c>IUserServiceRead</c>.
    /// </summary>
    Task<ShiftVolunteerProfileInfo?> GetShiftProfileAsync(Guid userId);

    /// <summary>
    /// Gets shift tags, optionally filtered by name (case-insensitive contains).
    /// </summary>
    Task<IReadOnlyList<ShiftTagSummary>> GetTagsAsync(string? query = null);

    /// <summary>
    /// Sets a volunteer's tag preferences, replacing any existing ones.
    /// </summary>
    Task SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds);
}

public record ShiftTagSummary(Guid Id, string Name);

/// <summary>
/// A volunteer's shift-matching lists, flattened off
/// <c>VolunteerEventProfile</c>. The entity's dietary and medical fields moved
/// to Profile before G5, so what is left of it that crosses the section
/// boundary is three string lists.
/// </summary>
public sealed record ShiftVolunteerProfileInfo(
    Guid UserId,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Quirks,
    IReadOnlyList<string> Languages)
{
    /// <summary>True when the volunteer has recorded nothing at all.</summary>
    public bool IsEmpty => Skills.Count == 0 && Quirks.Count == 0 && Languages.Count == 0;
}
