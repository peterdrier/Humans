namespace Humans.Application.Interfaces.Metering;

/// <summary>
/// Handle for a push-registered gauge. Returned by
/// <see cref="IMeters.Declare"/>; the declaring service holds it and calls
/// <see cref="Set"/> whenever the value changes. The handle lives for the
/// app's lifetime — there is no unregister path. Meters are process-wide
/// gauges; disposing individual handles would be a footgun for scoped
/// consumers whose field cleanup would unregister the singleton instrument.
/// </summary>
public interface IMeter
{
    /// <summary>Sets the current gauge value.</summary>
    void Set(int value);
}
