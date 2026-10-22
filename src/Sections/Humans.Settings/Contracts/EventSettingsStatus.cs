namespace Humans.Settings.Contracts;

/// <summary>
/// Deleting is a status change, never a row removal: other sections store the row's <c>Id</c>, so the row has to
/// survive.
/// </summary>
public enum EventSettingsStatus
{
    /// <summary>The current cycle. At most one row is <see cref="Active"/>.</summary>
    Active = 0,

    /// <summary>A past or future cycle. Still readable by id, never returned as active.</summary>
    Inactive = 1,

    /// <summary>Withdrawn. Never returned as active; the row and its id remain.</summary>
    Deleted = 2,
}
