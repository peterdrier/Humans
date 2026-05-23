using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Coordinates profile edit form saves around the Users-owned storage mutation.
/// </summary>
public interface IProfileEditorService : IApplicationService
{
    Task<Guid> SaveProfileAsync(
        Guid userId,
        string displayName,
        ProfileSaveRequest request,
        CancellationToken ct = default);
}
