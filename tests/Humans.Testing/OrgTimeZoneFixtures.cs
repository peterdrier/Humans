using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Testing;

/// <summary>A fixed <see cref="IOrgTimeZone"/> for tests; defaults to the production default zone.</summary>
public sealed class FixedOrgTimeZone(DateTimeZone zone) : IOrgTimeZone
{
    public static readonly FixedOrgTimeZone Madrid = new(DateTimeZoneProviders.Tzdb["Europe/Madrid"]);

    public DateTimeZone Zone { get; } = zone;
}
