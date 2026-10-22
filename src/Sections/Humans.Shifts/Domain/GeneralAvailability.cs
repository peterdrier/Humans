using NodaTime;

namespace Humans.Shifts.Domain;

/// <summary>One row per user per event.</summary>
internal sealed class GeneralAvailability
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid EventSettingsId { get; set; }
    public List<int> AvailableDayOffsets { get; set; } = [];
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }

    public EventSettings EventSettings { get; set; } = null!;
}
