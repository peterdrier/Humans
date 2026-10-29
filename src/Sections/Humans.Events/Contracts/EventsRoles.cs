using System.Security.Claims;
using Humans.Base.Authorization;

namespace Humans.Events.Contracts;

/// <summary>Events' grantable role and its predicate. Declared to Auth by Events' <c>Section</c>.</summary>
public static class EventsRoles
{
    /// <summary>Events Admin: approves, rejects and requests edits on event guide submissions.</summary>
    public const string EventsAdmin = "EventsAdmin";

    /// <summary>Admin or Events Admin.</summary>
    public static bool IsEventsAdmin(ClaimsPrincipal user) =>
        RoleChecks.IsAdmin(user) || user.IsInRole(EventsAdmin);
}
