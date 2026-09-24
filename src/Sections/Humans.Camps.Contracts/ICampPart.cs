using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Camps.Contracts;

/// <summary>A section-owned view component rendered onto a camp's detail page.</summary>
/// <remarks>
/// The component's Invoke parameters are bound from <see cref="CampPartArgs"/>. No slot name,
/// unlike <see cref="IUserPart"/>: there is exactly one host position today
/// (nobodies-collective/Humans#1815) — add one if a second host ever needs to pick a subset.
/// </remarks>
public sealed record CampPart(Type Component, int Weight = 0);

/// <summary>The arguments a <see cref="CampPart"/> component's Invoke/InvokeAsync accepts.</summary>
public sealed record CampPartArgs(Guid CampId);

/// <summary>
/// Camp parts a section contributes to the camp detail page (<c>Views/Camp/Details.cshtml</c>).
/// Returning nothing is the normal case.
/// </summary>
[ViewComponentSlot(typeof(CampPartArgs))]
public interface ICampPart : ISectionContribution
{
    IEnumerable<CampPart> Parts();
}
