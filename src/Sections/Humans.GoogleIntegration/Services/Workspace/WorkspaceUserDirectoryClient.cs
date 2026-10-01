using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Google.Apis.Services;
using Humans.Base.Extensions;
using Humans.Base.Configuration;
using Microsoft.Extensions.Options;

namespace Humans.GoogleIntegration.Services.Workspace;

/// <summary>
/// Real Google-backed implementation of <see cref="IWorkspaceUserDirectoryClient"/>.
/// Talks to the Google Workspace Admin SDK (Directory API) using the configured
/// service account. This is the only file that imports <c>Google.Apis.*</c> for
/// user-account management.
/// </summary>
internal sealed class WorkspaceUserDirectoryClient(
    IOptions<GoogleWorkspaceSettings> settings,
    ILogger<WorkspaceUserDirectoryClient> logger) : IWorkspaceUserDirectoryClient
{
    private readonly GoogleWorkspaceSettings _settings = settings.Value;
    private DirectoryService? _directoryService;

    private async Task<DirectoryService> GetDirectoryServiceAsync()
    {
        if (_directoryService is not null)
            return _directoryService;

        // Account mutations share this client; initialization retains its detached token.
        var credential = await GoogleCredentialLoader.LoadScopedAsync(
            _settings, CancellationToken.None,
            DirectoryService.Scope.AdminDirectoryUser,
            DirectoryService.Scope.AdminDirectoryUserSecurity);

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
        using var _ = logger.TimeOperation();
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
        using var _ = logger.TimeOperation();
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
        using var _ = logger.TimeOperation();
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
        using var _ = logger.TimeOperation();
        var service = await GetDirectoryServiceAsync();
        var update = new User { Suspended = true };
        await service.Users.Update(update, primaryEmail).ExecuteAsync(ct);
    }

    public async Task ReactivateAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        var service = await GetDirectoryServiceAsync();
        var update = new User { Suspended = false };
        await service.Users.Update(update, primaryEmail).ExecuteAsync(ct);
    }

    public async Task ResetPasswordAsync(
        string primaryEmail, string newPassword, CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        var service = await GetDirectoryServiceAsync();
        var update = new User
        {
            Password = newPassword,
            ChangePasswordAtNextLogin = true
        };
        await service.Users.Update(update, primaryEmail).ExecuteAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GenerateBackupCodesAsync(
        string primaryEmail, CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        var service = await GetDirectoryServiceAsync();

        // Generate issues a fresh set of codes. Google always issues 10 codes and
        // invalidates any previously issued set in the same call.
        await service.VerificationCodes.Generate(primaryEmail).ExecuteAsync(ct);

        var response = await service.VerificationCodes.List(primaryEmail).ExecuteAsync(ct);

        var codes = response.Items?
            .Select(c => c.VerificationCodeValue)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList() ?? [];

        return codes;
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
                : null,
            IsEnrolledIn2Sv: user.IsEnrolledIn2Sv ?? false,
            RecoveryEmail: string.IsNullOrWhiteSpace(user.RecoveryEmail) ? null : user.RecoveryEmail);
    }
}
