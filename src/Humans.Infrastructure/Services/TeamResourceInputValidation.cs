using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Services;

internal static class TeamResourceInputValidation
{
    public static Task<LinkResourceResult> LinkDriveResourceAsync(
        Guid teamId,
        string url,
        DrivePermissionLevel permissionLevel,
        CancellationToken ct,
        Func<Guid, string, DrivePermissionLevel, CancellationToken, Task<LinkResourceResult>> linkDriveFolderAsync,
        Func<Guid, string, DrivePermissionLevel, CancellationToken, Task<LinkResourceResult>> linkDriveFileAsync)
    {
        if (TeamResourceService.ParseDriveFolderId(url) is not null)
        {
            return linkDriveFolderAsync(teamId, url, permissionLevel, ct);
        }

        if (TeamResourceService.ParseDriveFileId(url) is not null)
        {
            return linkDriveFileAsync(teamId, url, permissionLevel, ct);
        }

        return Task.FromResult(new LinkResourceResult(false,
            ErrorMessage: TeamResourceValidationMessages.InvalidDriveUrl));
    }

    public static string? NormalizeGroupEmail(string? groupEmail)
    {
        if (string.IsNullOrWhiteSpace(groupEmail) || !groupEmail.Contains("@", StringComparison.Ordinal))
        {
            return null;
        }

        return groupEmail.Trim();
    }
}
