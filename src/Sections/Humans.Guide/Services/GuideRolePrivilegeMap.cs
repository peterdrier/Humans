using System.Diagnostics.CodeAnalysis;
using Humans.Auth.Contracts;

namespace Humans.Guide.Services;

/// <summary>
/// Maps the titles in guide-heading parentheticals ("Camps Admin") to privilege tokens: the role's name from the
/// <see cref="RoleCatalog"/> for every entry but <see cref="CampLead"/>.
/// </summary>
internal sealed class GuideRolePrivilegeMap(RoleCatalog catalog)
{
    /// <summary>
    /// A role on a camp season, never a claim. <c>GuideRoleResolver</c> sets it from <c>ICampLeadDirectory</c>.
    /// </summary>
    public const string CampLead = "CampLead";

    private const string CampLeadTitle = "Camp Lead";

    /// <summary>
    /// The roles <see cref="GuideRoleResolver"/> probes against claims. Excludes <see cref="CampLead"/>: no claim carries it.
    /// </summary>
    public IReadOnlyList<string> MappedRoles { get; } = [.. catalog.Roles.Select(r => r.Name)];

    public bool TryResolve(string displayName, [NotNullWhen(true)] out string? systemRole)
    {
        systemRole = null;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        if (string.Equals(displayName.Trim(), CampLeadTitle, StringComparison.OrdinalIgnoreCase))
        {
            systemRole = CampLead;
            return true;
        }

        systemRole = catalog.FindByTitle(displayName)?.Name;
        return systemRole is not null;
    }

    /// <summary>Unknown tokens are skipped, not thrown.</summary>
    public IReadOnlyList<string> ParseParenthetical(string? paren)
    {
        if (string.IsNullOrWhiteSpace(paren))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var token in paren.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryResolve(token, out var role))
            {
                result.Add(role);
            }
        }
        return result;
    }
}
