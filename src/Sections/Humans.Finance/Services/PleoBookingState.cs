using Humans.Finance.Models;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>Process-wide: the current or last booking run, and the Pleo ids this process posted (or may have posted)
/// that 57200003's ledger does not show yet. Holded's ledger is the record; this only covers the time between a
/// post and the re-pull that shows it.</summary>
internal sealed class PleoBookingState
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private PleoBookingRunVm? _run;

    public PleoBookingRunVm? Run
    {
        get { lock (_lock) return _run; }
    }

    public void Publish(PleoBookingRunVm run)
    {
        lock (_lock) _run = run;
    }

    public IReadOnlyList<string> Pending
    {
        get { lock (_lock) return _pending.Order(StringComparer.Ordinal).ToList(); }
    }

    public bool IsPending(string pleoId)
    {
        lock (_lock) return _pending.Contains(pleoId);
    }

    public void AddPending(string pleoId)
    {
        lock (_lock) _pending.Add(pleoId);
    }

    public void RemovePending(IEnumerable<string> pleoIds)
    {
        lock (_lock) _pending.ExceptWith(pleoIds);
    }

    public IReadOnlyList<string> ClearPending()
    {
        lock (_lock)
        {
            var cleared = _pending.Order(StringComparer.Ordinal).ToList();
            _pending.Clear();
            return cleared;
        }
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
