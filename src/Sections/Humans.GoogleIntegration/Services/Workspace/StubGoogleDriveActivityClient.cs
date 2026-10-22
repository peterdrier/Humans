using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services.Workspace;

internal sealed class StubGoogleDriveActivityClient(ILogger<StubGoogleDriveActivityClient> logger)
    : IGoogleDriveActivityClient
{
    public bool IsConfigured => false;

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
        logger.LogDebug(
            "[Stub] Drive activity query for {GoogleItemId} since {Since} — no Google credentials configured",
            googleItemId, sinceIsoTimestamp);
        yield break;
    }
#pragma warning restore CS1998

    public Task<string?> TryResolvePersonEmailAsync(string peopleId, CancellationToken ct = default)
    {
        // Pass through email-shaped inputs (the real connector does the same).
        return Task.FromResult(
            peopleId.StartsWith("people/", StringComparison.Ordinal) ? null : peopleId);
    }
}
