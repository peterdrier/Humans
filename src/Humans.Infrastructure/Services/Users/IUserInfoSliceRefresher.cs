using System.Runtime.CompilerServices;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Services.Users;

/// <summary>
/// Infrastructure-internal cache-staleness signal used by
/// <c>UserInfoSaveChangesInterceptor</c> to apply slice-level refreshes to the
/// <see cref="CachingUserService"/> dict when Identity / OAuth / direct-repo
/// writes touch contributing tables. Split out from
/// <c>IUserInfoInvalidator</c> (§15e — one-method cross-section signal) so the
/// public Application-layer interface stays a single <c>InvalidateAsync</c>
/// surface; the six slice methods are an implementation detail of the
/// interceptor–decorator conversation and must not become a cross-section
/// contract.
/// </summary>
internal interface IUserInfoSliceRefresher
{
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

    Task RefreshEventParticipationsAsync(
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
