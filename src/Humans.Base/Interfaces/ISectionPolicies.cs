using Microsoft.AspNetCore.Authorization;

namespace Humans.Base.Interfaces;

/// <summary>
/// The authorization policies a section owns. Policy <em>names</em> stay shared vocabulary
/// in <c>PolicyNames</c> — nav items cite other sections' policies — only registration moves.
/// </summary>
/// <remarks>
/// Shell applies these through <c>Configure&lt;AuthorizationOptions&gt;</c>, which is
/// additive, so cross-section policies keep registering centrally.
/// </remarks>
public interface ISectionPolicies : ISectionContribution
{
    void AddPolicies(AuthorizationOptions options);
}
