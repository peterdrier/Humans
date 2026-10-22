using System.Runtime.CompilerServices;
using Humans.Users.Contracts;

namespace Humans.Users.Data;

/// <summary>
/// Refreshes <see cref="CachingUserService"/> entries for writes that bypass the decorator.
/// Every contributing table is Users-owned: it must not become a cross-section contract.
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
