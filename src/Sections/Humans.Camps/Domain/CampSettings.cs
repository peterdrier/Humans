using NodaTime;

namespace Humans.Camps.Domain;

internal sealed class CampSettings
{
    public Guid Id { get; init; }

    /// <summary>
    /// Dead column (nobodies-collective#1632): no longer read or written. The active event
    /// year (<see cref="Humans.Settings.Contracts.ISettingsService"/>) replaced it. Mapped
    /// only so the EF model matches the database; dropping the column is a separate,
    /// Peter-approved follow-up.
    /// </summary>
    public int PublicYear { get; set; }
    public List<int> OpenSeasons { get; set; } = [];

    /// <summary>
    /// Dead column (nobodies-collective#1633): no longer read or written. Replaced by
    /// <c>EventSettings.EarlyEntryStartOffset</c>. Mapped only so the EF model matches the
    /// database; dropping the column is a separate, Peter-approved follow-up.
    /// </summary>
    public LocalDate? EeStartDate { get; set; }
}
