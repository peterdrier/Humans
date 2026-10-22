using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Camps.Contracts;

/// <summary>A section-owned view component rendered onto a camp's detail page.</summary>
public sealed record CampPart(Type Component, int Weight = 0);

public sealed record CampPartArgs(Guid CampId);

/// <summary>Contributions to the camp detail page. Returning nothing is the normal case.</summary>
[ViewComponentSlot(typeof(CampPartArgs))]
public interface ICampPart : ISectionContribution
{
    IEnumerable<CampPart> Parts();
}
