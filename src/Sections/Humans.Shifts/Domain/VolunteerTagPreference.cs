namespace Humans.Shifts.Domain;

internal sealed class VolunteerTagPreference
{
    public Guid Id { get; init; }

    /// <summary>Settable so the account-merge fold can re-FK it.</summary>
    public Guid UserId { get; set; }

    public Guid ShiftTagId { get; init; }

    public ShiftTag ShiftTag { get; set; } = null!;
}
