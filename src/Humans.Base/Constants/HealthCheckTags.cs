namespace Humans.Base.Constants;

/// <summary>
/// Tags applied to <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration"/>
/// entries, shared so sections and Shell agree on the literal.
/// </summary>
public static class HealthCheckTags
{
    /// <summary>
    /// Marks a check as third-party reachability: diagnostic-only on <c>/health</c>, excluded
    /// from <c>/health/ready</c> — a vendor outage must never fail the readiness probe.
    /// </summary>
    public const string External = "external";
}
