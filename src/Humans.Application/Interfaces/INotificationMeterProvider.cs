using System.Security.Claims;
using Humans.Application.DTOs;

namespace Humans.Application.Interfaces;

/// <summary>
/// Provides live counter meters for admin/coordinator work queues.
/// Meters are computed at query time with caching — no DB storage, no read state.
/// </summary>
public interface INotificationMeterProvider
{
    /// <summary>
    /// Returns all meters visible to the current user based on their roles.
    /// Only returns meters with count > 0.
    /// </summary>
    Task<IReadOnlyList<NotificationMeter>> GetMetersForUserAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default);
}
