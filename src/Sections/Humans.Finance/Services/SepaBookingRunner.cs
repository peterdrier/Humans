using Humans.Finance.Models;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>Runs a <c>/Finance/Sepa</c> booking off the request, so a slow Holded never holds the browser and leaving
/// the page never stops a half-posted booking. One at a time: a second start while one runs is refused here, and
/// <see cref="SepaBankBooking"/>'s gate still serializes it against the sweep. Process-wide, like
/// <see cref="PleoBookingRunner"/>: a restart loses the last outcome, never a posting, which the audit log keeps.</summary>
internal sealed class SepaBookingRunner(IServiceScopeFactory scopes, IClock clock, ILogger<SepaBookingRunner> logger)
{
    private readonly Lock _lock = new();
    private SepaBookingRunVm? _run;

    public SepaBookingRunVm? Run
    {
        get { lock (_lock) return _run; }
    }

    public bool TryStart(string what, Func<IFinanceAdminService, Task<SepaBookingResult>> booking)
    {
        lock (_lock)
        {
            if (_run is { IsRunning: true }) return false;
            _run = new SepaBookingRunVm(what, clock.GetCurrentInstant(), null, null);
        }

        // Runs on this thread only until its first Holded call yields, then off the request.
        _ = RunAsync(booking);
        return true;
    }

    private async Task RunAsync(Func<IFinanceAdminService, Task<SepaBookingResult>> booking)
    {
        SepaBookingResult result;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            result = await booking(scope.ServiceProvider.GetRequiredService<IFinanceAdminService>());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SEPA booking run failed");
            result = new SepaBookingResult(false,
                $"The booking stopped on an unexpected error ({ex.Message}). Check the audit log for a PARTIAL "
                + "entry before booking again.");
        }

        lock (_lock) _run = _run! with { FinishedAt = clock.GetCurrentInstant(), Result = result };
    }
}
