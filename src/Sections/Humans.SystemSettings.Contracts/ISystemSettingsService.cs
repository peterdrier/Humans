using Humans.Application.Interfaces;

namespace Humans.SystemSettings.Contracts;

/// <summary>
/// Application boundary for shared key/value system settings, and SystemSettings'
/// entire cross-section surface: <c>EmailOutboxService</c> (Email) and
/// <c>DriveActivityMonitorService</c> (GoogleIntegration) both read and write
/// through it. Read and write sit on one interface for the duration of the G5
/// rollout — splitting the surface is deferred until every section has moved
/// (G5-SECTION-TEMPLATE.md step 5b).
/// </summary>
public interface ISystemSettingsService : IApplicationService
{
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);
}
