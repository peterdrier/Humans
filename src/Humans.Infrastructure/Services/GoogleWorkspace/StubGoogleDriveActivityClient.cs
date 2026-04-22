using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Stub implementation of <see cref="IGoogleDriveActivityClient"/> for
/// development environments without Google service-account credentials.
/// Returns an empty activity stream and benign service-account metadata so
/// the monitor service can run end-to-end locally without hitting Google.
/// Per the §15 connector pattern (PR #274/#287), the real Application-layer
/// <c>DriveActivityMonitorService</c> runs against this stub — there is no
/// separate "stub service" variant.
/// </summary>
public sealed class StubGoogleDriveActivityClient : IGoogleDriveActivityClient
{
    private readonly ILogger<StubGoogleDriveActivityClient> _logger;

    public StubGoogleDriveActivityClient(ILogger<StubGoogleDriveActivityClient> logger)
    {
        _logger = logger;
    }

    public Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default)
        => Task.FromResult("stub-service-account@example.iam.gserviceaccount.com");

    public Task<string?> GetServiceAccountClientIdAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(null);

#pragma warning disable CS1998 // async lacks await — required to match IAsyncEnumerable contract
    public async IAsyncEnumerable<DriveActivityEvent> QueryActivityAsync(
        string googleItemId,
        string sinceIsoTimestamp,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[Stub] Drive activity query for {GoogleItemId} since {Since} — no Google credentials configured",
            googleItemId, sinceIsoTimestamp);
        yield break;
    }
#pragma warning restore CS1998

    public Task<string?> TryResolvePersonEmailAsync(string peopleId, CancellationToken ct = default)
    {
        // Pass through email-shaped inputs (the real connector does the same).
        return Task.FromResult<string?>(
            peopleId.StartsWith("people/", StringComparison.Ordinal) ? null : peopleId);
    }
}
