using NodaTime;

namespace Humans.Camps.Domain;

internal sealed class CampSettings
{
    public Guid Id { get; init; }

    /// <summary>
    /// Dead column: no longer read or written (the active event year replaced it). Mapped only so the EF model
    /// matches the database; dropping it is a separate, Peter-approved follow-up.
    /// </summary>
    public int PublicYear { get; set; }
    public List<int> OpenSeasons { get; set; } = [];

    /// <summary>
    /// Dead column: no longer read or written (<c>EventSettings.EarlyEntryStartOffset</c> replaced it). Mapped only so
    /// the EF model matches the database; dropping it is a separate, Peter-approved follow-up.
    /// </summary>
    public LocalDate? EeStartDate { get; set; }
}
