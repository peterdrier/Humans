namespace Humans.Auth.Contracts;

/// <summary>
/// Result of a role-assignment mutation: <c>Success</c> plus a caller-mapped
/// <c>ErrorKey</c> for the failure the caller is expected to render.
/// </summary>
/// <remarks>
/// Auth owns this type rather than reusing <c>Humans.Onboarding.Contracts.OnboardingResult</c>:
/// Auth.Contracts must not reference Onboarding.Contracts (<c>peters-hard-rules.md</c>), and
/// role assignment is not part of the onboarding funnel.
/// </remarks>
public record RoleAssignmentResult(bool Success, string? ErrorKey = null);
