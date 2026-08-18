namespace Humans.Application.Metering;

/// <summary>
/// Descriptor for an <see cref="Interfaces.Metering.IMeters.Declare"/> call.
/// <see cref="Description"/> and <see cref="Unit"/> form the OpenTelemetry export
/// contract — <c>Unit</c> follows OTel curly-brace conventions (<c>{events}</c>,
/// <c>{profiles}</c>).
/// </summary>
public sealed record MeterMetadata(string Description, string Unit);
