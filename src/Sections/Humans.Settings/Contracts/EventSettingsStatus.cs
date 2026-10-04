namespace Humans.Settings.Contracts;

/// <summary>
/// Lifecycle of one event cycle's settings row.
/// </summary>
/// <remarks>
/// Deleting is a status change, never a row removal: other sections store the
/// row's <c>Id</c> (<c>Rota.EventSettingsId</c>,
/// <c>EventGuideSettings.EventSettingsId</c>), so the row and its id have to
/// survive. Removing rows outright may be allowed in dev/QA; it is not a
/// production operation.
/// </remarks>
public enum EventSettingsStatus
{
    /// <summary>The current cycle. At most one row is <see cref="Active"/>.</summary>
    Active = 0,

    /// <summary>A past or future cycle. Still readable by id, never returned as active.</summary>
    Inactive = 1,

    /// <summary>Withdrawn. Never returned as active; the row and its id remain.</summary>
    Deleted = 2,
}
