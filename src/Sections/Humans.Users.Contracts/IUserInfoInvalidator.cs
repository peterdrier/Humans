using System.Runtime.CompilerServices;

namespace Humans.Users.Contracts;

/// <summary>
/// One-way cache-staleness signal for <see cref="UserInfo"/>. Implemented by
/// <c>CachingUserService</c> (<c>Humans.Users/Data/</c>). External writers that change any
/// of the contributing tables (<c>users</c>, <c>user_emails</c>,
/// <c>event_participations</c>, <c>user_logins</c>, <c>profiles</c>,
/// <c>contact_fields</c>, <c>profile_languages</c>,
/// <c>volunteer_history_entries</c>, <c>communication_preferences</c>) inject this and call
/// <see cref="InvalidateAsync"/> after their writes. The decorator reloads the
/// affected entry from those tables, preserving the fully-warm invariant.
/// </summary>
/// <remarks>
/// Sole cross-section cache-staleness signal for the unified User+Profile cache.
/// Slice-level refresh entry points used by <c>UserInfoSaveChangesInterceptor</c> live on the
/// section-internal <c>IUserInfoSliceRefresher</c> — they are not part of the cross-section
/// contract and must not be added here.
/// </remarks>
// No IInvalidator marker — see Humans.Users.Contracts.csproj. Debt
// nobodies-collective/Humans#805: cross-section UserInfo flush; retire once CachingUserService
// owns invalidation end-to-end.
public interface IUserInfoInvalidator
{
    Task InvalidateAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "");
}
