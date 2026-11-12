using Humans.Shifts.Contracts;
namespace Humans.Shifts.Services;

/// <summary>
/// Carries writes, so it keeps an honest name rather than a <c>Read</c> suffix. Users' profile edit page is the
/// consumer.
/// </summary>
internal interface IShiftVolunteerProfiles
{
    /// <summary>Null when the user has none.</summary>
    Task<ShiftVolunteerProfileInfo?> GetShiftProfileAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Case-insensitive contains on name.</summary>
    Task<IReadOnlyList<ShiftTagSummary>> GetTagsAsync(string? query = null, CancellationToken ct = default);

    /// <summary>Replaces existing preferences; false when any requested tag does not exist, leaving them unchanged.</summary>
    Task<bool> SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds);
}

internal sealed record ShiftVolunteerProfileInfo(
    Guid UserId,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Quirks,
    IReadOnlyList<string> Languages)
{
    /// <summary>Nothing recorded at all.</summary>
    public bool IsEmpty => Skills.Count == 0 && Quirks.Count == 0 && Languages.Count == 0;
}
