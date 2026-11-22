using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;

namespace Humans.Users.Services;

/// <summary>
/// The one reader of a member's medical conditions (GDPR Art. 9). The text is not on the cached
/// <c>UserInfo</c>, so no other section can see it; anyone but the owner gets it only through
/// <see cref="RevealAsync"/>, which records who looked.
/// </summary>
internal sealed class MedicalConditionsService(
    IUserRepository repo,
    IUserService userService,
    IAuditLogService auditLogService) : IApplicationService
{
    public async Task<string?> GetOwnAsync(Guid userId, CancellationToken ct = default) =>
        (await repo.GetByUserIdReadOnlyAsync(userId, ct))?.MedicalConditions;

    /// <summary>Null when the member is unknown or nothing is on file; then nothing is audited.</summary>
    public async Task<string?> RevealAsync(Guid subjectUserId, Guid actorUserId, CancellationToken ct = default)
    {
        // A merged-away id resolves to the survivor, whose id is what the member's own page lists.
        var subject = await userService.GetUserInfoAsync(subjectUserId, ct);
        if (subject is null) return null;

        var text = await GetOwnAsync(subject.Id, ct);
        if (string.IsNullOrEmpty(text)) return null;

        await auditLogService.LogAsync(
            UsersAuditActions.MedicalConditionsRevealed, AuditSubjectTypes.User, subject.Id,
            $"Medical conditions revealed for user {subject.Id}", actorUserId);
        return text;
    }
}
