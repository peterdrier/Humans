using Humans.Google.Contracts;
using Humans.Workgroups.Domain;

namespace Humans.Workgroups.Services.Contributors;

/// <summary>
/// A group's optional email group holds its current members, through the same fan-out Teams and Camps use. Groups that
/// never reached Active are not claimed; a Dormant group's roster is frozen, so its email group keeps that roster.
/// </summary>
internal sealed class WorkgroupGoogleGroupSource(IWorkgroupService workgroups) : IGoogleGroupMembershipSource
{
    public async Task<Dictionary<string, Guid[]>> GetExpectedAsync(string? groupKey = null, CancellationToken ct = default)
    {
        var requestedKey = string.IsNullOrWhiteSpace(groupKey) ? null : groupKey.Trim();
        var register = await workgroups.GetRegisterAsync(ct);

        return register
            .Where(w => w.GoogleGroupEmail is not null && IsClaimed(w.Status))
            .Where(w => requestedKey is null
                || string.Equals(w.GoogleGroupEmail, requestedKey, StringComparison.OrdinalIgnoreCase))
            // The service refuses a shared group; should one slip through, neither claims it, so sync fails closed.
            .GroupBy(w => w.GoogleGroupEmail!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().CurrentMemberUserIds().ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsClaimed(WorkgroupStatus status) =>
        status is WorkgroupStatus.Active or WorkgroupStatus.Dormant;
}
