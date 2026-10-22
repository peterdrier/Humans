namespace Humans.GoogleIntegration.Services.Workspace;

internal sealed class StubTeamResourceGoogleClient(ILogger<StubTeamResourceGoogleClient> logger)
    : ITeamResourceGoogleClient
{
    public Task<DriveLookupResult> GetDriveItemAsync(
        string itemId,
        bool expectFolder,
        CancellationToken ct = default)
    {
        logger.LogInformation("[STUB] Would look up Drive item {ItemId} (expectFolder={ExpectFolder})",
            itemId, expectFolder);

        // The stub can't discover the MIME type, so it fabricates one matching the caller's intent, with a
        // WebViewLink shaped like a real Drive URL so the stored Url is clickable in dev/QA.
        var webViewLink = expectFolder
            ? $"https://drive.google.com/drive/folders/{itemId}"
            : $"https://drive.google.com/file/d/{itemId}/view";

        return Task.FromResult(new DriveLookupResult(
            new DriveItem(
                Id: itemId,
                Name: itemId,
                WebViewLink: webViewLink,
                IsFolder: expectFolder,
                FullPath: itemId),
            Error: null));
    }

    public Task<GroupLookupResult> LookupGroupAsync(string groupEmail, CancellationToken ct = default)
    {
        logger.LogInformation("[STUB] Would look up Group {GroupEmail}", groupEmail);

        // Echo the email back as numeric id so the service's dedup-by-(id,email)
        // logic still works in dev.
        var emailLocal = groupEmail.Split('@')[0];
        return Task.FromResult(new GroupLookupResult(
            new ResolvedGroup(
                NumericId: groupEmail,
                NormalizedEmail: groupEmail,
                DisplayName: groupEmail,
                DisplayUrl: $"https://groups.google.com/a/nobodies.team/g/{emailLocal}"),
            Error: null));
    }

    public Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default)
    {
        return Task.FromResult("stub-service-account@project.iam.gserviceaccount.com");
    }
}
