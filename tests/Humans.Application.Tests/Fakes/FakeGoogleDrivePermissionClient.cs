using System.Net;
using Google;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Requests;
using Humans.Infrastructure.GoogleSync;

namespace Humans.Application.Tests.Fakes;

/// <summary>
/// In-memory fake for <see cref="IGoogleDrivePermissionClient"/> used by reconciliation tests.
/// State is keyed by Drive file id and tracks both direct and inherited permissions so tests
/// can verify the direct/inherited split that reconciliation relies on.
/// </summary>
internal sealed class FakeGoogleDrivePermissionClient : IGoogleDrivePermissionClient
{
    private readonly Dictionary<string, List<Permission>> _state = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failingFiles = new(StringComparer.Ordinal);

    public IReadOnlyList<Permission> GetPermissions(string fileId) =>
        _state.TryGetValue(fileId, out var list)
            ? list.ToList()
            : [];

    /// <summary>
    /// Seeds a direct (non-inherited) user permission on the given file.
    /// </summary>
    public void SeedDirectPermission(string fileId, string email, string role = "writer")
    {
        if (!_state.TryGetValue(fileId, out var list))
        {
            list = [];
            _state[fileId] = list;
        }

        list.Add(new Permission
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "user",
            Role = role,
            EmailAddress = email,
            PermissionDetails = null
        });
    }

    /// <summary>
    /// Seeds an inherited permission — reconciliation treats these as read-only and
    /// classifies them as <c>Inherited</c>, never <c>Extra</c>.
    /// </summary>
    public void SeedInheritedPermission(string fileId, string email, string role = "reader")
    {
        if (!_state.TryGetValue(fileId, out var list))
        {
            list = [];
            _state[fileId] = list;
        }

        list.Add(new Permission
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "user",
            Role = role,
            EmailAddress = email,
            PermissionDetails =
            [
                new Permission.PermissionDetailsData { Inherited = true, Role = role }
            ]
        });
    }

    /// <summary>
    /// Registers a file id whose List/Create/Delete operations all throw a generic exception.
    /// Used to exercise the error-path branches in SyncDriveResourceGroupAsync and the
    /// post-execute softDeletedTeamIds filter.
    /// </summary>
    public void FailFile(string fileId) => _failingFiles.Add(fileId);

    public Task<IReadOnlyList<Permission>> ListPermissionsAsync(string fileId, CancellationToken cancellationToken)
    {
        if (_failingFiles.Contains(fileId))
            throw BuildApiException(HttpStatusCode.NotFound, $"File {fileId} not found.");

        return Task.FromResult<IReadOnlyList<Permission>>(GetPermissions(fileId));
    }

    public Task<string> CreatePermissionAsync(string fileId, string userEmail, string role, CancellationToken cancellationToken)
    {
        if (_failingFiles.Contains(fileId))
            throw BuildApiException(HttpStatusCode.Forbidden, "Simulated failure on CreatePermission.");

        if (!_state.TryGetValue(fileId, out var list))
        {
            list = [];
            _state[fileId] = list;
        }

        var id = Guid.NewGuid().ToString("N");
        list.Add(new Permission
        {
            Id = id,
            Type = "user",
            Role = role,
            EmailAddress = userEmail,
            PermissionDetails = null
        });
        return Task.FromResult(id);
    }

    public Task DeletePermissionAsync(string fileId, string permissionId, CancellationToken cancellationToken)
    {
        if (_failingFiles.Contains(fileId))
            throw BuildApiException(HttpStatusCode.Forbidden, "Simulated failure on DeletePermission.");

        if (!_state.TryGetValue(fileId, out var list))
            return Task.CompletedTask;

        list.RemoveAll(p => string.Equals(p.Id, permissionId, StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static GoogleApiException BuildApiException(HttpStatusCode code, string message)
    {
        var error = new RequestError
        {
            Code = (int)code,
            Message = message
        };
        return new GoogleApiException("Drive", message)
        {
            Error = error,
            HttpStatusCode = code
        };
    }
}
