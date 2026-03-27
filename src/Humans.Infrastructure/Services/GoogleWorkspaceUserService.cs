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
/// The service account authenticates as itself (no delegation or impersonation)
/// and must be assigned the User Management Admin role in Google Workspace Admin Console.
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

        _logger.LogInformation("Listed {Count} @{Domain} accounts", accounts.Count, _settings.Domain);
        return accounts;
    }

    public async Task<WorkspaceUserAccount> ProvisionAccountAsync(
        string primaryEmail,
        string firstName,
        string lastName,
        string temporaryPassword,
        string? recoveryEmail = null,
        CancellationToken ct = default)
    {
        var service = await GetDirectoryServiceAsync();

        // Google Directory API requires non-empty FamilyName; fall back to GivenName
        var sanitizedLastName = string.IsNullOrWhiteSpace(lastName) ? firstName : lastName;

        var newUser = new User
        {
            PrimaryEmail = primaryEmail,
            Name = new UserName
            {
                GivenName = firstName,
                FamilyName = sanitizedLastName
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
        _logger.LogInformation("Provisioned @{Domain} account: {Email} (recovery: {Recovery})",
            _settings.Domain, primaryEmail, recoveryEmail ?? "none");

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
        // Users.Get() returns 403 for our service account, but Users.List() with
        // a query filter works. Use that to check if an account exists.
        var service = await GetDirectoryServiceAsync();

        var request = service.Users.List();
        request.Domain = _settings.Domain;
        request.Query = $"email={primaryEmail}";
        request.MaxResults = 1;

        var response = await request.ExecuteAsync(ct);
        var user = response.UsersValue?.FirstOrDefault();

        if (user is null)
        {
            _logger.LogDebug("Workspace account not found for email {Email}", primaryEmail);
            return null;
        }

        return MapToAccount(user);
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
