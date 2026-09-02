namespace Humans.Rideshare.Domain;

/// <summary>Pending → Accepted / Declined / Withdrawn. Only Accepted consumes seats.</summary>
internal enum InterestStatus
{
    Pending,
    Accepted,
    Declined,
    Withdrawn
}
