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
    /// True only for an admin-set PIN, the only kind <c>AuthorizeOverrideAsync</c> accepts (nothing outside tests calls it;
    /// kiosk overrides use the shared <c>Gate:SupervisorPin</c>). A kiosk self-enrolment gets false.
    /// </summary>
    public bool AdminEnrolled { get; set; }
}
