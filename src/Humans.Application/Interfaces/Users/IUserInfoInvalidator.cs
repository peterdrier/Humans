using Humans.Application.Architecture;

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
/// Sole cross-section cache-staleness signal for the unified User+Profile
/// cache (§15e). The legacy <c>IFullProfileInvalidator</c> was retired
/// alongside the FullProfile delete; every external section that previously
/// held it now holds this. Slice-level refresh entry points used by
/// <c>UserInfoSaveChangesInterceptor</c> live on the Infrastructure-internal
/// <c>IUserInfoSliceRefresher</c> — they are not part of the cross-section
/// contract and must not be added here.
/// </remarks>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing user-info cache flushed cross-section; remains until UserService's caching decorator owns invalidation end-to-end.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface IUserInfoInvalidator : IInvalidator
{
    Task InvalidateAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "");
}
