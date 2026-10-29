namespace Humans.Cantina.Contracts;

/// <summary>Cantina's grantable role. Declared to Auth by Cantina's <c>Section</c>.</summary>
public static class CantinaRoles
{
    /// <summary>
    /// Cantina Admin: views the weekly/daily roster (on-site cohort, dietary breakdown, allergy and intolerance
    /// rollups) to plan meals. Read-only; sees dietary preferences but never medical conditions.
    /// </summary>
    public const string CantinaAdmin = "CantinaAdmin";
}
