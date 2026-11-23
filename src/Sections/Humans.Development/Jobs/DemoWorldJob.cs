using Humans.Development.Services.Demo;
using NodaTime;

namespace Humans.Development.Jobs;

/// <summary>Runs the demo build in the background, so a long build never holds the admin's request.</summary>
internal sealed class DemoWorldJob(
    DemoWorldRunner runner,
    DemoRunStatus status,
    IClock clock,
    ILogger<DemoWorldJob> logger)
{
    public async Task ExecuteAsync(bool reset, Guid actorUserId, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> lines = ["Failed before finishing; see the logs."];
        try
        {
            lines = await runner.RunAsync(reset, actorUserId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Demo world run failed");
            lines = [$"Failed: {ex.Message}"];
        }
        finally
        {
            status.Finish(lines, clock.GetCurrentInstant());
        }
    }
}
