using System.Security.Claims;

namespace Humans.Base.Interfaces;

/// <summary>A section-owned view component rendered on a user's profile page.</summary>
/// <remarks>
/// The component must accept a <c>Guid userId</c> argument. It owns authorization for any
/// data beyond the profile's ordinary authenticated-user visibility and returns empty content
/// when it has nothing to show. Users supplies the target profile id; contributors never infer
/// it from the viewer.
/// </remarks>
public sealed record UserPart(string ComponentName, int Weight = 0);

/// <summary>
/// Profile parts a section contributes for a target user. Returning nothing is the normal case.
/// </summary>
public interface IUserPart : ISectionContribution
{
    ValueTask<IEnumerable<UserPart>> PartsAsync(
        IServiceProvider services,
        ClaimsPrincipal viewer,
        Guid userId);
}
