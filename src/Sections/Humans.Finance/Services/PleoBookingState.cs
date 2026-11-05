using Humans.Finance.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Finance.Services;

/// <summary>Process-wide: the current or last booking run, for the page to follow.</summary>
internal sealed class PleoBookingState
{
    private readonly Lock _lock = new();
    private PleoBookingRunVm? _run;

    public PleoBookingRunVm? Run
    {
        get { lock (_lock) return _run; }
    }

    public void Publish(PleoBookingRunVm run)
    {
        lock (_lock) _run = run;
    }
}

/// <summary>Runs a booking off the request, so it survives the page being left. One at a time: a second start while
/// one is going is refused here, and <see cref="PleoBooking"/>'s own gate refuses any other caller.</summary>
internal sealed class PleoBookingRunner(IServiceScopeFactory scopes, ILogger<PleoBookingRunner> logger)
{
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public bool TryStart(int? limit, Guid actorUserId)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PleoBooking>()
                    .BookAsync(limit, actorUserId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pleo booking run failed");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
        return true;
    }
}
