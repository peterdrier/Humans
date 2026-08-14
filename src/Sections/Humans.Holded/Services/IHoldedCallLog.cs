using NodaTime;

namespace Humans.Holded.Services;

internal sealed record HoldedApiCallRecord(
    Instant CalledAt, string Endpoint, string Method, int StatusCode,
    int? RateLimitRemaining, string? RateLimitWindow);

/// <summary>In-memory buffer of Holded API calls. The client appends; the Holded section's service
/// drains to holded_api_calls. Singleton; loses at most the unflushed tail on crash (GET /usage is
/// the authoritative counter).</summary>
internal interface IHoldedCallLog
{
    void Record(HoldedApiCallRecord record);
    IReadOnlyList<HoldedApiCallRecord> DrainAll();
}
