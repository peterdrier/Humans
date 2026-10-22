using NodaTime;

namespace Humans.Holded.Services;

internal sealed record HoldedApiCallRecord(
    Instant CalledAt, string Endpoint, string Method, int StatusCode,
    int? RateLimitRemaining, string? RateLimitWindow);

/// <summary>The client appends; the service drains to holded_api_calls. A crash loses at most the unflushed tail
/// (GET /usage stays the authoritative count).</summary>
internal interface IHoldedCallLog
{
    void Record(HoldedApiCallRecord record);
    IReadOnlyList<HoldedApiCallRecord> DrainAll();
}
