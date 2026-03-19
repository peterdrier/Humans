using Humans.Application.DTOs;

namespace Humans.Infrastructure.Services;

internal static class TeamResourceInputValidation
{
    public static Task<LinkResourceResult> LinkDriveResourceAsync(
        Guid teamId,
        string url,
        CancellationToken ct,
        Func<Guid, string, CancellationToken, Task<LinkResourceResult>> linkDriveFolderAsync,
        Func<Guid, string, CancellationToken, Task<LinkResourceResult>> linkDriveFileAsync)
    {
        if (TeamResourceService.ParseDriveFolderId(url) != null)
        {
            return linkDriveFolderAsync(teamId, url, ct);
        }

        if (TeamResourceService.ParseDriveFileId(url) != null)
        {
            return linkDriveFileAsync(teamId, url, ct);
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
