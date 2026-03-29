namespace Humans.Application.Interfaces;

/// <summary>
/// Marker interface for Hangfire recurring jobs. All jobs must expose ExecuteAsync
/// as their Hangfire entry point.
/// </summary>
public interface IRecurringJob
{
    Task ExecuteAsync(CancellationToken cancellationToken = default);
}
