namespace Humans.Application.DTOs;

/// <summary>
/// Authorization scope for global search. Replaces the prohibited
/// <c>isPrivileged</c> boolean pattern (see
/// <c>memory/code/authorization-conventions.md</c>) — every call site reads
/// <c>SearchScope.Public</c> or <c>SearchScope.Admin</c> literally and is
/// auditable at a glance, mirroring the <c>PersonSearchFields</c> precedent
/// in <c>memory/architecture/person-search.md</c>.
/// </summary>
/// <remarks>
/// The auth boundary is the controller, not the service. A non-admin
/// endpoint passing <see cref="Admin"/> is a programmer error caught in
/// code review, not a runtime check.
/// </remarks>
public enum SearchScope
{
    /// <summary>
    /// Public viewer scope. Hidden teams are excluded; only camps with a
    /// public season status (<c>Active</c> / <c>Full</c>) for the public
    /// year are surfaced; only volunteer-visible rotas are surfaced;
    /// human results use <c>PersonSearchFields.PublicAll</c>.
    /// </summary>
    Public,

    /// <summary>
    /// Admin viewer scope. Hidden teams are surfaced; camps in any season
    /// status are surfaced; non-volunteer-visible rotas are surfaced;
    /// human results use <c>PersonSearchFields.AdminAll</c>. Set only by
    /// controllers that have already authorized an admin-shaped role.
    /// </summary>
    Admin,
}
