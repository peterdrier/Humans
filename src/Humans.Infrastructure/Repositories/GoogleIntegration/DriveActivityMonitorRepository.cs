using Humans.Application.Extensions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.SystemSettings;
using Humans.Domain.Constants;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Repositories.GoogleIntegration;

/// <summary>
/// Compatibility adapter for the old Drive monitor repository contract.
/// The SystemSettings section owns persistence; this wrapper only preserves the
/// public boundary for callers that have not moved to ISystemSettingsService.
/// </summary>
internal sealed class DriveActivityMonitorRepository(
    ISystemSettingsService systemSettings,
    ILogger<DriveActivityMonitorRepository> logger) : IDriveActivityMonitorRepository
{
    internal const string LastRunSettingKey = SystemSettingKeys.DriveActivityMonitorLastRunAt;

    public async Task<Instant?> GetLastRunTimestampAsync(CancellationToken ct = default)
    {
        var value = await systemSettings.GetValueAsync(LastRunSettingKey, ct);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var pattern = NodaTime.Text.InstantPattern.General;
        var result = pattern.Parse(value);
        if (result.Success)
        {
            return result.Value;
        }

        logger.LogWarning(
            "Could not parse stored Drive activity monitor timestamp '{Value}', falling back to default lookback",
            value);
        return null;
    }

    public Task AdvanceLastRunMarkerAsync(
        Instant? newLastRunAt,
        CancellationToken ct = default)
    {
        return newLastRunAt is null
            ? Task.CompletedTask
            : systemSettings.SetValueAsync(
                LastRunSettingKey,
                newLastRunAt.Value.ToInvariantInstantString(),
                ct);
    }
}
