using NodaTime;

namespace Humans.Holded.Domain;

/// <summary>The per-month, per-endpoint breakdown GET /usage doesn't give. /usage stays the authoritative count.</summary>
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
