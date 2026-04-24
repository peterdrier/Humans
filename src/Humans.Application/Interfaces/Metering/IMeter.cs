namespace Humans.Application.Interfaces.Metering;

/// <summary>
/// Handle for a push-registered gauge. Returned by
/// <see cref="IMeters.Declare"/>; the declaring service holds it and calls
/// <see cref="Set"/> whenever the value changes. Disposing removes the meter
/// from the registry.
/// </summary>
public interface IMeter : IDisposable
{
    /// <summary>Sets the current gauge value.</summary>
    void Set(int value);
}
