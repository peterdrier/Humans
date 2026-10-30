using Microsoft.AspNetCore.Authorization;

namespace Humans.Base.Interfaces;

/// <summary>
/// The authorization policies a section owns. Their names live on the section's
/// <c>&lt;Section&gt;Policies</c> class in its Contracts, so other sections cite them from there.
/// </summary>
/// <remarks>
/// Shell applies these through <c>Configure&lt;AuthorizationOptions&gt;</c>, which is
/// additive, so cross-section policies keep registering centrally.
/// </remarks>
public interface ISectionPolicies : ISectionContribution
{
    void AddPolicies(AuthorizationOptions options);
}
