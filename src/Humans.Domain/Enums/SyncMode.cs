namespace Humans.Domain.Enums;

/// <summary>
/// Controls what automated sync jobs do for a given service.
/// </summary>
public enum SyncMode
{
    /// <summary>No automatic sync — jobs skip this service entirely.</summary>
    None = 0,
    /// <summary>Automated jobs only add missing members.</summary>
    AddOnly = 1,
    /// <summary>Automated jobs add missing and remove extra members.</summary>
    AddAndRemove = 2
}
