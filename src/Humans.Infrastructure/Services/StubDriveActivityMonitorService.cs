using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Stub implementation of IDriveActivityMonitorService for development without Google credentials.
/// </summary>
public class StubDriveActivityMonitorService : IDriveActivityMonitorService
{
    private readonly ILogger<StubDriveActivityMonitorService> _logger;

    public StubDriveActivityMonitorService(ILogger<StubDriveActivityMonitorService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<int> CheckForAnomalousActivityAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[STUB] Drive activity monitor check — no Google credentials configured");
        return Task.FromResult(0);
    }
}
