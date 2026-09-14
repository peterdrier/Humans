namespace Humans.Rideshare.Domain;

/// <summary>Stored trip status. "Full" is derived from accepted interests, never stored.</summary>
internal enum TripStatus
{
    Active,
    Cancelled
}
