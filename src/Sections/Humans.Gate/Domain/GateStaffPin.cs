using NodaTime;

namespace Humans.Gate.Domain;

/// <summary>
/// Confirms who takes over the scanner on a shift change; supervisor overrides use the shared
/// <c>Gate:SupervisorPin</c> instead. Only the hash is stored.
/// </summary>
internal sealed class GateStaffPin
{
    public Guid UserId { get; init; }

    public string PinHash { get; set; } = string.Empty;

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// True only for an admin-set PIN, the one that can carry override authority. A kiosk self-enrolment gets false, so
    /// cold-setting a supervisor's PIN at the anonymous kiosk gains attribution only.
    /// </summary>
    public bool AdminEnrolled { get; set; }
}
