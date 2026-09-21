using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.CityPlanning.Authorization;

/// <summary>
/// Succeeds for CampAdmin/Admin, OR for a member of the city-planning team — the same
/// audience <c>CityPlanningController.RequireMapAdminAsync</c> admits. Backs
/// <see cref="PolicyNames.CityPlanningMapAdmin"/> so the <c>/Settings#city-planning</c>
/// tab shows to exactly the people whose POST endpoints still accept them
/// (peterdrier/Humans#1634).
/// </summary>
internal sealed class CityPlanningMapAdminRequirement : IAuthorizationRequirement;
