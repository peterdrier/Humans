namespace Humans.Camps.Domain;

/// <summary>Camps' own settings for one Settings event row. Absent row = registration closed.</summary>
internal sealed class CampEventSettings
{
    /// <summary>Bare id of Settings' <c>settings_events</c> row; also the primary key.</summary>
    public Guid EventSettingsId { get; init; }

    public bool RegistrationOpen { get; set; }

    /// <summary>
    /// Markdown shown at the top of the Register page while this event takes registrations. Empty means an admin
    /// cleared it; null means it was never set, so City Planning's old copy still applies until
    /// <c>camps.registration-info-from-city-planning</c> retires.
    /// </summary>
    public string? RegistrationInfo { get; set; }
}
