namespace Humans.Domain.Constants;

/// <summary>
/// Well-known IDs for system-managed camps.
/// </summary>
public static class SystemCampIds
{
    // Reserved GUID block: 0011. See docs/guid-reservations.md.
    // The "Organization" camp is a virtual holder for org-level containers
    // (containers not tied to any barrio). It has no seasons, members, or
    // polygons; it exists so Container.CampId can be non-nullable and the
    // service surface needs only one set of methods (no GetOrg* variants).
    public static readonly Guid Organization = Guid.Parse("00000000-0000-0000-0011-000000000001");
}
