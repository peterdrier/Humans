using System.Runtime.CompilerServices;
using Humans.Users.Contracts;

namespace Humans.Users.Data;

/// <summary>
/// Section-internal cache-staleness signal into the <see cref="CachingUserService"/> dict.
/// <c>UserInfoSaveChangesInterceptor</c> applies slice-level refreshes when Identity / OAuth /
/// direct-repo writes touch contributing tables; Users' own services that write past the
/// decorator (GDPR erasure, import provisioning, merge rejection, contact fields) call
/// <see cref="RefreshAsync"/>. Every contributing table is Users-owned, so no other section
/// needs it: it must not become a cross-section contract.
/// </summary>
internal interface IUserInfoSliceRefresher
{
    /// <summary>Reloads the whole entry from the contributing tables, removing it when the user is gone.</summary>
    Task RefreshAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "");

    Task RefreshUserFieldsAsync(
        User user,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "");

    Task RemoveAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "");

    Task RefreshUserEmailsAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "");

    Task RefreshExternalLoginsAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "");

    Task RefreshCommunicationPreferencesAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "");
}
