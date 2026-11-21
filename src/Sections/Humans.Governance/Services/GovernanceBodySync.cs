using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Google.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;

namespace Humans.Governance.Services;

/// <summary>
/// Asks Google to bring a body's group and files up to date after its roster changes; anything missed waits for the
/// hourly group sweep and the nightly Drive run.
/// </summary>
internal sealed class GovernanceBodySync(
    IGovernanceBodyRepository links,
    IGoogleGroupSync groups,
    IGoogleSyncWrite drive) : IApplicationService
{
    public async Task RequestAsync(GovernanceBody body, CancellationToken ct = default)
    {
        if ((await links.GetAllAsync(ct)).SingleOrDefault(l => l.Body == body) is not { } stored)
            return;

        if (stored.GoogleGroupEmail is { } email)
            await groups.RequestSyncAsync(email, ct, GoogleSyncSource.SystemTeamSync);
        foreach (var file in stored.Files)
            await drive.RequestSyncAsync(file.GoogleId, ct);
    }
}
