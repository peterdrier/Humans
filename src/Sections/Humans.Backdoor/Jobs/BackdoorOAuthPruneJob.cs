using Hangfire;
using Humans.Base.Interfaces;
using NodaTime;
using OpenIddict.Abstractions;

namespace Humans.Backdoor.Jobs;

/// <summary>Deletes expired, spent and revoked sign-in tokens, and connections that no longer work.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class BackdoorOAuthPruneJob(
    IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager connections,
    IHumansMetrics metrics,
    ILogger<BackdoorOAuthPruneJob> logger,
    IClock clock) : IRecurringJob
{
    /// <summary>Anything younger is kept, so a token refused a moment ago can still be looked at.</summary>
    private static readonly Duration Grace = Duration.FromDays(1);

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var threshold = (clock.GetCurrentInstant() - Grace).ToDateTimeOffset();
            var tokenCount = await tokens.PruneAsync(threshold, cancellationToken);
            var connectionCount = await connections.PruneAsync(threshold, cancellationToken);

            metrics.RecordJobRun("backdoor_oauth_prune", "success");
            logger.LogInformation(
                "Sign-in prune finished; deleted {TokenCount} token(s) and {ConnectionCount} connection(s)",
                tokenCount, connectionCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Sign-in prune cancelled during shutdown");
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("backdoor_oauth_prune", "failure");
            logger.LogError(ex, "Error during sign-in prune");
            throw;
        }
    }
}
