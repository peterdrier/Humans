using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Records which days a volunteer is generally available for an event.
/// One record per user per event. Used by coordinators to see pool of available volunteers.
/// </summary>
public class GeneralAvailability
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid EventSettingsId { get; set; }
    public List<int> AvailableDayOffsets { get; set; } = [];
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }

    // Navigation properties.
    // Cross-domain nav User stripped per design-rules §6c (Shifts owns
    // general_availability; User lives in Users/Identity). Callers that need
    // user display data resolve it through IUserService/IProfileService.
    // EventSettings is section-local (Shifts) — kept as an aggregate nav per
    // docs/sections/Shifts.md.
    public EventSettings EventSettings { get; set; } = null!;
}
