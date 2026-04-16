using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;

namespace Humans.Infrastructure.GoogleSync;

/// <summary>
/// Production <see cref="IGoogleDrivePermissionClient"/> that delegates to a
/// <see cref="DriveService"/>. Always sets <c>SupportsAllDrives = true</c> and requests
/// <c>permissionDetails</c> so the caller can distinguish direct from inherited Shared Drive
/// permissions.
/// </summary>
internal sealed class RealGoogleDrivePermissionClient : IGoogleDrivePermissionClient
{
    private readonly DriveService _service;

    public RealGoogleDrivePermissionClient(DriveService service)
    {
        _service = service;
    }

    public async Task<IReadOnlyList<Permission>> ListPermissionsAsync(string fileId, CancellationToken cancellationToken)
    {
        var result = new List<Permission>();
        string? pageToken = null;
        do
        {
            var request = _service.Permissions.List(fileId);
            request.SupportsAllDrives = true;
            request.Fields = "nextPageToken, permissions(id, emailAddress, role, type, permissionDetails)";
            if (pageToken is not null)
                request.PageToken = pageToken;

            var response = await request.ExecuteAsync(cancellationToken);
            if (response.Permissions is not null)
                result.AddRange(response.Permissions);

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return result;
    }

    public async Task<string> CreatePermissionAsync(string fileId, string userEmail, string role, CancellationToken cancellationToken)
    {
        var permission = new Permission
        {
            Type = "user",
            Role = role,
            EmailAddress = userEmail
        };

        var request = _service.Permissions.Create(permission, fileId);
        request.SupportsAllDrives = true;
        request.SendNotificationEmail = false;
        var created = await request.ExecuteAsync(cancellationToken);
        return created.Id;
    }

    public async Task DeletePermissionAsync(string fileId, string permissionId, CancellationToken cancellationToken)
    {
        var request = _service.Permissions.Delete(fileId, permissionId);
        request.SupportsAllDrives = true;
        await request.ExecuteAsync(cancellationToken);
    }
}
