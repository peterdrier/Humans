namespace Humans.Rideshare.Domain;

/// <summary>Stored request status. "Matched" is derived from accepted interests, never stored.</summary>
internal enum RequestStatus
{
    Active,
    Cancelled
}
