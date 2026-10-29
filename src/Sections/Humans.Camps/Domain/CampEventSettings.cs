namespace Humans.Camps.Domain;

/// <summary>Camps' own settings for one Settings event row. Absent row = registration closed.</summary>
internal sealed class CampEventSettings
{
    /// <summary>Bare id of Settings' <c>settings_event</c> row; also the primary key.</summary>
    public Guid EventSettingsId { get; init; }

    public bool RegistrationOpen { get; set; }
}
