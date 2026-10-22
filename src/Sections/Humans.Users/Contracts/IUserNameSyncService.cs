using Humans.Base.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>Copies names still held only on <c>Profile</c> onto <c>User</c>. Idempotent.</summary>
public interface IUserNameSyncService : IApplicationService
{
    Task<IReadOnlyList<UnsyncedNameRow>> GetUnsyncedAsync(CancellationToken ct = default);

    /// <summary>Audits each save to the acting admin. Returns how many were synced.</summary>
    Task<int> SyncAllAsync(Guid actorUserId, CancellationToken ct = default);
}

public sealed record UnsyncedNameRow(
    Guid UserId,
    string Email,
    UserState State,
    string ProfileBurnerName,
    string ProfileLegalName,
    bool BurnerNameMissing,
    bool LegalNameMissing);
