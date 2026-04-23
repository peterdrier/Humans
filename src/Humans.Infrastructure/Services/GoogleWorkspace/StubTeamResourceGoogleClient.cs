using Humans.Application.Interfaces.GoogleIntegration;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Dev/test <see cref="ITeamResourceGoogleClient"/> that returns synthetic
/// responses without talking to Google. Matches the pre-migration behaviour
/// of <c>StubTeamResourceService</c>: a linked Drive resource's <c>Name</c>
/// becomes the id, the <c>Url</c> is echoed back, and every call succeeds
/// (no network round-trip, no 404/403 handling).
/// </summary>
public sealed class StubTeamResourceGoogleClient : ITeamResourceGoogleClient
{
    private readonly ILogger<StubTeamResourceGoogleClient> _logger;

    public StubTeamResourceGoogleClient(ILogger<StubTeamResourceGoogleClient> logger)
    {
        _logger = logger;
    }

    public Task<DriveLookupResult> GetDriveItemAsync(
        string itemId,
        bool expectFolder,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Would look up Drive item {ItemId} (expectFolder={ExpectFolder})",
            itemId, expectFolder);

        // Without Google, the stub cannot discover the real MIME type. Mirror
        // the pre-migration StubTeamResourceService behavior by fabricating a
        // response whose type matches the caller's intent — the service's MIME
        // check then passes and the original dev-time linking flow succeeds
        // without a round-trip. Synthesize a plausible WebViewLink so the
        // persisted GoogleResource.Url is clickable in dev/QA (the pre-
        // migration StubTeamResourceService kept the user-submitted URL; we
        // can't plumb that here so we build one that matches real Drive URL
        // shape for the caller's expected item kind).
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
        _logger.LogInformation("[STUB] Would look up Group {GroupEmail}", groupEmail);

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
