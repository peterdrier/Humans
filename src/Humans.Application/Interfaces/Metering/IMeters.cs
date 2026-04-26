using Humans.Application.Metering;

namespace Humans.Application.Interfaces.Metering;

/// <summary>
/// Process-wide gauge registry. Leaf node in the DI graph: depends on
/// <c>ILogger</c> only, never on any other service. Services that own a system-wide
/// gauge inject <see cref="IMeters"/>, call <see cref="Declare"/> in their
/// constructor to obtain an <see cref="IMeter"/> handle, and push values via
/// <see cref="IMeter.Set"/>. Gauges are automatically exported to OpenTelemetry
/// under the existing <c>Meter("Humans.Metrics")</c> subscription.
/// </summary>
/// <remarks>
/// Not a scoped concern. A meter is a system-wide number with a single current
/// value; lifecycle is process lifetime. <see cref="Declare"/> is idempotent by
/// name so scoped callers can re-declare safely on each scope creation — the
/// same handle is returned.
/// </remarks>
public interface IMeters
{
    /// <summary>
    /// Declares (or returns) a push-registered gauge. Idempotent by
    /// <paramref name="name"/> — calling with an already-registered name returns
    /// the existing handle.
    /// </summary>
    IMeter Declare(string name, MeterMetadata metadata);
}
