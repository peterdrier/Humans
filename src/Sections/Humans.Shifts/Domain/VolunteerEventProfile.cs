using NodaTime;

namespace Humans.Shifts.Domain;

/// <summary>Skills, quirks and languages for shift-matching. One per user.</summary>
internal sealed class VolunteerEventProfile
{
    public Guid Id { get; init; }

    /// <summary>Settable so the account-merge fold can re-FK it.</summary>
    public Guid UserId { get; set; }

    public List<string> Skills { get; set; } = [];

    public List<string> Quirks { get; set; } = [];

    public List<string> Languages { get; set; } = [];

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }
}
