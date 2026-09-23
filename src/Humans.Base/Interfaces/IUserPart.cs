using Humans.Base.Attributes;

namespace Humans.Base.Interfaces;

/// <summary>The argument a <see cref="IUserPart"/> component's Invoke/InvokeAsync accepts.</summary>
public sealed record UserPartArgs(Guid UserId);

/// <summary>A section-owned view component rendered on a user's profile page.</summary>
/// <remarks>
/// The component's Invoke parameters are bound from <see cref="UserPartArgs"/> (<c>Guid userId</c>).
/// It owns authorization for any data beyond the profile's ordinary authenticated-user visibility
/// and returns empty content when it has nothing to show. Users supplies the target profile id;
/// contributors never infer it from the viewer.
/// </remarks>
public sealed record UserPart(Type Component, int Weight = 0);

/// <summary>
/// Profile parts a section contributes for a target user. Returning nothing is the normal case.
/// </summary>
[ViewComponentSlot(typeof(UserPartArgs))]
public interface IUserPart : ISectionContribution
{
    IEnumerable<UserPart> Parts();
}
