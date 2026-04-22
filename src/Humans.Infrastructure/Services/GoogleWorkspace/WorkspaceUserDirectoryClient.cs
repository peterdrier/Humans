using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Options;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Real Google-backed implementation of <see cref="IWorkspaceUserDirectoryClient"/>.
/// Talks to the Google Workspace Admin SDK (Directory API) using the configured
/// service account. This is the only file that imports <c>Google.Apis.*</c> for
/// user-account management; the Application-layer service never sees SDK types.
/// </summary>
public sealed class WorkspaceUserDirectoryClient : IWorkspaceUserDirectoryClient
{
    private readonly GoogleWorkspaceSettings _settings;
    private DirectoryService? _directoryService;

    public WorkspaceUserDirectoryClient(IOptions<GoogleWorkspaceSettings> settings)
    {
        _settings = settings.Value;
    }

    private async Task<DirectoryService> GetDirectoryServiceAsync()
    {
        if (_directoryService is not null)
            return _directoryService;

        var credential = await GetCredentialAsync();

        _directoryService = new DirectoryService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _directoryService;
    }

    private async Task<GoogleCredential> GetCredentialAsync()
    {
        GoogleCredential credential;

        if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            using var stream = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(_settings.ServiceAccountKeyJson));
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(
                stream, CancellationToken.None).ConfigureAwait(false)).ToGoogleCredential();
        }
        else if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyPath))
        {
            await using var stream = File.OpenRead(_settings.ServiceAccountKeyPath);
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(
                stream, CancellationToken.None).ConfigureAwait(false)).ToGoogleCredential();
        }
        else
        {
            throw new InvalidOperationException(
                "Google Workspace credentials not configured. Set ServiceAccountKeyPath or ServiceAccountKeyJson.");
        }

        return credential.CreateScoped(DirectoryService.Scope.AdminDirectoryUser);
    }

    public async Task<IReadOnlyList<WorkspaceUserAccount>> ListAccountsAsync(
        CancellationToken ct = default)
    {
        var service = await GetDirectoryServiceAsync();
        var accounts = new List<WorkspaceUserAccount>();
        string? pageToken = null;

        do
        {
            var request = service.Users.List();
            request.Domain = _settings.Domain;
            request.MaxResults = 500;
            request.OrderBy = UsersResource.ListRequest.OrderByEnum.Email;
            if (pageToken is not null)
                request.PageToken = pageToken;

            var response = await request.ExecuteAsync(ct);

            if (response.UsersValue is not null)
            {
                foreach (var user in response.UsersValue)
                {
                    accounts.Add(MapToAccount(user));
                }
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return accounts;
    }

    public async Task<WorkspaceUserAccount?> GetAccountAsync(
        string primaryEmail, CancellationToken ct = default)
    {
        // Users.Get() returns 403 for our service account, but Users.List() with
        // a query filter works. Use that to check if an account exists.
        var service = await GetDirectoryServiceAsync();

        var request = service.Users.List();
        request.Domain = _settings.Domain;
        request.Query = $"email={primaryEmail}";
        request.MaxResults = 1;

        var response = await request.ExecuteAsync(ct);
        var user = response.UsersValue?.FirstOrDefault();
        return user is null ? null : MapToAccount(user);
    }

    public async Task<WorkspaceUserAccount> ProvisionAccountAsync(
        string primaryEmail,
        string firstName,
        string lastName,
        string temporaryPassword,
        string? recoveryEmail,
        CancellationToken ct = default)
    {
        var service = await GetDirectoryServiceAsync();

        var newUser = new User
        {
            PrimaryEmail = primaryEmail,
            Name = new UserName
            {
                GivenName = firstName,
                FamilyName = lastName
            },
            Password = temporaryPassword,
            ChangePasswordAtNextLogin = true,
            OrgUnitPath = "/"
        };

        // Set recovery email if provided (for password resets and initial notification)
        if (!string.IsNullOrEmpty(recoveryEmail) &&
            !recoveryEmail.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
        {
            newUser.RecoveryEmail = recoveryEmail;
        }

        var created = await service.Users.Insert(newUser).ExecuteAsync(ct);
        return MapToAccount(created);
    }

    public async Task SuspendAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        var service = await GetDirectoryServiceAsync();
        var update = new User { Suspended = true };
        await service.Users.Update(update, primaryEmail).ExecuteAsync(ct);
    }

    public async Task ReactivateAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        var service = await GetDirectoryServiceAsync();
        var update = new User { Suspended = false };
        await service.Users.Update(update, primaryEmail).ExecuteAsync(ct);
    }

    public async Task ResetPasswordAsync(
        string primaryEmail, string newPassword, CancellationToken ct = default)
    {
        var service = await GetDirectoryServiceAsync();
        var update = new User
        {
            Password = newPassword,
            ChangePasswordAtNextLogin = true
        };
        await service.Users.Update(update, primaryEmail).ExecuteAsync(ct);
    }

    private static WorkspaceUserAccount MapToAccount(User user)
    {
        return new WorkspaceUserAccount(
            PrimaryEmail: user.PrimaryEmail,
            FirstName: user.Name?.GivenName ?? string.Empty,
            LastName: user.Name?.FamilyName ?? string.Empty,
            IsSuspended: user.Suspended ?? false,
            CreationTime: user.CreationTimeRaw is not null
                ? DateTime.Parse(user.CreationTimeRaw, System.Globalization.CultureInfo.InvariantCulture)
                : DateTime.MinValue,
            LastLoginTime: user.LastLoginTimeRaw is not null
                ? DateTime.Parse(user.LastLoginTimeRaw, System.Globalization.CultureInfo.InvariantCulture)
                : null);
    }
}
