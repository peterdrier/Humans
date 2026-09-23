using Humans.Base.Interfaces;

namespace Humans.Base.Models;

/// <summary>The parts one user-part slot renders, already ordered, and the args each is invoked with.</summary>
internal sealed record UserPartsViewModel(UserPartArgs Args, IReadOnlyList<UserPart> Parts);
