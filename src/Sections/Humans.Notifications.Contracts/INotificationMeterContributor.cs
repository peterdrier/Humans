using System.Security.Claims;
using Humans.Base.Interfaces;

namespace Humans.Notifications.Contracts;

/// <summary>
/// A section's live counts for the viewer's notification meters. Notifications asks every registered contributor
/// on each read and never reaches into the section's data, so the count and its caching stay with the owner.
/// Return only meters whose count is above zero.
/// </summary>
public interface INotificationMeterContributor : IFanout
{
    Task<IReadOnlyList<NotificationMeter>> GetMetersAsync(
        ClaimsPrincipal viewer, Guid viewerId, CancellationToken ct = default);
}
