using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Settings.Services;

/// <summary>
/// The active event's switched-off sections, held in memory because every request asks. Flushed by every
/// event-settings write, which covers both an edited list and a different event becoming active.
/// </summary>
internal sealed class SectionSwitches(IServiceScopeFactory scopeFactory, ILogger<SectionSwitches> logger)
    : ISectionSwitches, IEventSettingsChangeListener
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();
    private IReadOnlySet<string>? _switchedOff;
    private int _version;

    public async ValueTask<IReadOnlySet<string>> GetSwitchedOffAsync(CancellationToken cancellationToken = default)
    {
        int version;
        lock (_gate)
        {
            if (_switchedOff is { } cached)
                return cached;

            version = _version;
        }

        EventSettingsInfo? active;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            active = await scope.ServiceProvider.GetRequiredService<ISettingsService>()
                .GetActiveEventSettingsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Every page asks, including sign-in and the error pages, which must keep rendering with the database
            // down. Nothing is cached, so the next request asks again.
            logger.LogWarning(ex, "Could not read the switched-off sections; treating every section as on");
            return None;
        }

        IReadOnlySet<string> loaded = active?.SwitchedOffSections is { Count: > 0 } names
            ? names.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : None;

        lock (_gate)
        {
            // A write that landed while this read was in flight wins: leave the cache empty for the next caller.
            if (version == _version)
                _switchedOff = loaded;
        }

        return loaded;
    }

    public void EventSettingsChanged(Guid eventSettingsId)
    {
        lock (_gate)
        {
            _switchedOff = null;
            _version++;
        }
    }
}
