namespace Humans.Settings.Contracts;

/// <summary>Fixture seeding for test hosts. Writes no audit entry: fixtures have no real actor.</summary>
public interface IEventSettingsSeeding
{
    /// <summary>Deactivates whatever else is active.</summary>
    Task CreateActiveEventAsync(EventSettingsInfo settings, CancellationToken cancellationToken = default);
}
