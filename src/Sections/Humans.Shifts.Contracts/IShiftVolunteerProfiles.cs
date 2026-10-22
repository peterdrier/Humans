namespace Humans.Shifts.Contracts;

/// <summary>
/// Carries writes, so it keeps an honest name rather than a <c>Read</c> suffix. Users' profile edit page is the
/// consumer.
/// </summary>
public interface IShiftVolunteerProfiles
{
    /// <summary>Null when the user has none. Dietary and medical data are Profile's.</summary>
    Task<ShiftVolunteerProfileInfo?> GetShiftProfileAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Case-insensitive contains on name.</summary>
    Task<IReadOnlyList<ShiftTagSummary>> GetTagsAsync(string? query = null, CancellationToken ct = default);

    /// <summary>Replaces any existing ones.</summary>
    Task SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds);
}

public record ShiftTagSummary(Guid Id, string Name);

public sealed record ShiftVolunteerProfileInfo(
    Guid UserId,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Quirks,
    IReadOnlyList<string> Languages)
{
    /// <summary>Nothing recorded at all.</summary>
    public bool IsEmpty => Skills.Count == 0 && Quirks.Count == 0 && Languages.Count == 0;
}
