namespace Humans.Settings.Contracts;

/// <summary>Dev-fixture seeding for <c>Humans.Development</c>. Writes no audit entry: fixtures have no real actor.</summary>
public interface IEventSettingsSeeding
{
    /// <summary>Deactivates whatever else is active.</summary>
    Task CreateActiveEventAsync(EventSettingsInfo settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns rows deleted (0 or 1). Order against <c>IShiftSeeding.DeleteEventAsync</c> doesn't matter: there is no
    /// DB-level FK between the two sections' tables.
    /// </summary>
    Task<int> DeleteEventAsync(Guid id, CancellationToken cancellationToken = default);
}
