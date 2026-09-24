using Humans.Camps.Contracts;

namespace Humans.Camps.Models;

/// <summary>The parts the camp detail page renders, already ordered, and the args each is invoked with.</summary>
internal sealed record CampPartsViewModel(CampPartArgs Args, IReadOnlyList<CampPart> Parts);
