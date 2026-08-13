namespace Humans.Auth.Contracts;

/// <summary>
/// Result of a role-assignment mutation: <c>Success</c> plus a caller-mapped
/// <c>ErrorKey</c> for the failure the caller is expected to render.
/// </summary>
/// <remarks>
/// Shape-identical to <c>Humans.Onboarding.Contracts.OnboardingResult</c>, which
/// <c>AssignRoleAsync</c>/<c>EndRoleAsync</c> returned while the service was in Base. That
/// type cannot follow the section: Onboarding is a *vertical* section and Auth is a
/// horizontal one, so <c>Humans.Auth.Contracts</c> referencing
/// <c>Humans.Onboarding.Contracts</c> is the reference <c>peters-hard-rules.md</c> forbids
/// outright. It was never Auth's vocabulary either — the three services that give
/// <c>OnboardingResult</c> its name are the onboarding funnel's own siblings; role
/// assignment is not one of them. The section owns its boundary type and the two outside
/// call sites (Shell's <c>UsersAdminController</c> and <c>Humans.Development</c>'s
/// <c>DevPersonaSeeder</c>) read the same two members they already read.
/// </remarks>
public record RoleAssignmentResult(bool Success, string? ErrorKey = null);
