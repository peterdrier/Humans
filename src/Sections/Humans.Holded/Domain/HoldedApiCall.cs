using NodaTime;

namespace Humans.Holded.Domain;

/// <summary>
/// One recorded call to the Holded API, drained here from the in-memory call log so the admin
/// screen can show consumption per month and endpoint. Holded's own GET /usage stays the
/// authoritative counter — this is the breakdown it does not give us.
/// </summary>
internal sealed class HoldedApiCall
{
    public Guid Id { get; init; }
    public Instant CalledAt { get; init; }
    public string Endpoint { get; init; } = "";
    public string Method { get; init; } = "";
    public int StatusCode { get; init; }
    public int? RateLimitRemaining { get; init; }
    public string? RateLimitWindow { get; init; }
}
