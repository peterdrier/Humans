using Humans.Base.Attributes;
using Microsoft.AspNetCore.Http;

namespace Humans.Base.Interfaces;

/// <summary>
/// The sections an admin switched off for the active event. Softer than <see cref="ISection.IsActive"/>: a
/// switched-off section stays composed (services and data stay, so the sections that consume it keep working), but
/// its pages answer 404, nothing it contributes to nav, dashboards or admin renders, and its recurring jobs skip
/// their runs unless marked <see cref="Humans.Base.Attributes.StaysOnWhenSectionIsOffAttribute"/>.
/// </summary>
/// <remarks>Settings owns the list, because it owns the event row the list is stored on.</remarks>
public interface ISectionSwitches
{
    /// <summary>Section names as <see cref="ISectionCatalog"/> spells them. Empty with no active event.</summary>
    ValueTask<IReadOnlySet<string>> GetSwitchedOffAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Carries one request's switched-off set from the MVC filter that loads it to the view components that compose
/// section contributions, so they filter synchronously and all see the same answer.
/// </summary>
public static class SectionSwitchboard
{
    private static readonly object ItemsKey = new();

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static void SetSwitchedOffSections(this HttpContext httpContext, IReadOnlySet<string> switchedOff) =>
        httpContext.Items[ItemsKey] = switchedOff;

    /// <summary>Empty when nothing loaded a set for this request: an unfiltered page, never a broken one.</summary>
    public static IReadOnlySet<string> SwitchedOffSections(this HttpContext? httpContext) =>
        httpContext?.Items[ItemsKey] as IReadOnlySet<string> ?? None;

    /// <summary>Drops the contributions of sections switched off for this request.</summary>
    public static IEnumerable<T> SwitchedOn<T>(this IEnumerable<T> contributions, HttpContext? httpContext)
        where T : class
    {
        var switchedOff = httpContext.SwitchedOffSections();
        return switchedOff.Count == 0
            ? contributions
            : contributions.Where(c => !switchedOff.Contains(SectionNameOf(c.GetType())));
    }

    /// <summary>
    /// The section a type ships in, from its assembly name (<c>Humans.Cantina</c> is <c>Cantina</c>). A type outside
    /// any section (Shell, Base) yields a name no section has, so it is never switched off; so does one marked
    /// <see cref="StaysOnWhenSectionIsOffAttribute"/>.
    /// </summary>
    public static string SectionNameOf(Type type)
    {
        if (type.IsDefined(typeof(StaysOnWhenSectionIsOffAttribute), inherit: false))
            return type.FullName ?? type.Name;

        var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
        return assemblyName.StartsWith("Humans.", StringComparison.Ordinal)
            ? assemblyName["Humans.".Length..]
            : assemblyName;
    }
}
