using Humans.Base.Interfaces;

namespace Humans.Users.Models;

/// <summary>The section-contributed parts a profile page renders for one user, already ordered.</summary>
internal sealed record UserPartsViewModel(Guid UserId, IReadOnlyList<UserPart> Parts);
