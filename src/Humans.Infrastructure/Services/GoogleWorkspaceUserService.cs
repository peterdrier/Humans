using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Manages @nobodies.team user accounts via Google Workspace Admin SDK (Directory API).
/// Requires domain-wide delegation on the service account with the Admin SDK scope,
/// and an AdminEmail configured to impersonate.
/// </summary>
public class GoogleWorkspaceUserService : IGoogleWorkspaceUserService
{
    private readonly GoogleWorkspaceSettings _settings;
    private readonly ILogger<GoogleWorkspaceUserService> _logger;
    private DirectoryService? _directoryService;

    public GoogleWorkspaceUserService(
        IOptions<GoogleWorkspaceSettings> settings,
        ILogger<GoogleWorkspaceUserService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    private async Task<DirectoryService> GetDirectoryServiceAsync()
    {
        if (_directoryService != null)
            return _directoryService;

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
                "Google Workspace credentials not configured.");
        }

        // Admin SDK requires domain-wide delegation — impersonate an admin user
        if (string.IsNullOrEmpty(_settings.AdminEmail))
        {
            throw new InvalidOperationException(
                "GoogleWorkspace:AdminEmail must be set for Admin SDK operations. " +
                "This should be an admin user email for domain-wide delegation.");
        }

        credential = credential
            .CreateScoped(DirectoryService.Scope.AdminDirectoryUser)
            .CreateWithUser(_settings.AdminEmail);

        _directoryService = new DirectoryService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _directoryService;
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
            if (pageToken != null)
                request.PageToken = pageToken;

            var response = await request.ExecuteAsync(ct);

            if (response.UsersValue != null)
            {
                foreach (var user in response.UsersValue)
                {
                    accounts.Add(MapToAccount(user));
                }
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        _logger.LogInformation("Listed {Count} @{Domain} accounts", accounts.Count, _settings.Domain);
        return accounts;
    }

    public async Task<WorkspaceUserAccount> ProvisionAccountAsync(
        string primaryEmail,
        string firstName,
        string lastName,
        string temporaryPassword,
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

        var created = await service.Users.Insert(newUser).ExecuteAsync(ct);
        _logger.LogInformation("Provisioned @{Domain} account: {Email}", _settings.Domain, primaryEmail);

        return MapToAccount(created);
    }

    public async Task SuspendAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        var service = await GetDirectoryServiceAsync();

        var update = new User { Suspended = true };
        await service.Users.Update(update, primaryEmail).ExecuteAsync(ct);

        _logger.LogInformation("Suspended @{Domain} account: {Email}", _settings.Domain, primaryEmail);
    }

    public async Task ReactivateAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        var service = await GetDirectoryServiceAsync();

        var update = new User { Suspended = false };
        await service.Users.Update(update, primaryEmail).ExecuteAsync(ct);

        _logger.LogInformation("Reactivated @{Domain} account: {Email}", _settings.Domain, primaryEmail);
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

        _logger.LogInformation("Reset password for @{Domain} account: {Email}",
            _settings.Domain, primaryEmail);
    }

    public async Task<WorkspaceUserAccount?> GetAccountAsync(
        string primaryEmail, CancellationToken ct = default)
    {
        var service = await GetDirectoryServiceAsync();

        try
        {
            var user = await service.Users.Get(primaryEmail).ExecuteAsync(ct);
            return MapToAccount(user);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static WorkspaceUserAccount MapToAccount(User user)
    {
        return new WorkspaceUserAccount(
            PrimaryEmail: user.PrimaryEmail,
            FirstName: user.Name?.GivenName ?? string.Empty,
            LastName: user.Name?.FamilyName ?? string.Empty,
            IsSuspended: user.Suspended ?? false,
            CreationTime: user.CreationTimeRaw != null
                ? DateTime.Parse(user.CreationTimeRaw, System.Globalization.CultureInfo.InvariantCulture)
                : DateTime.MinValue,
            LastLoginTime: user.LastLoginTimeRaw != null
                ? DateTime.Parse(user.LastLoginTimeRaw, System.Globalization.CultureInfo.InvariantCulture)
                : null);
    }
}
