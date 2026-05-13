using System.Runtime.CompilerServices;

namespace Humans.Application.Interfaces.Users;

/// <summary>
/// One-way cache-staleness signal for <see cref="UserInfo"/>. Implemented by
/// the caching decorator in Infrastructure. External writers that change any
/// of the 8 contributing tables (<c>users</c>, <c>user_emails</c>,
/// <c>event_participations</c>, <c>user_logins</c>, <c>profiles</c>,
/// <c>contact_fields</c>, <c>profile_languages</c>,
/// <c>volunteer_history_entries</c>) inject this and call
/// <see cref="InvalidateAsync"/> after their writes. The decorator reloads the
/// affected entry from the 8 tables, preserving the fully-warm invariant.
/// </summary>
/// <remarks>
/// Sibling of <see cref="Profiles.IFullProfileInvalidator"/>. Both coexist for
/// the issue-703 PR — <c>FullProfile</c> follow-up migration is out of scope.
/// </remarks>
public interface IUserInfoInvalidator
{
    Task InvalidateAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "");
}
