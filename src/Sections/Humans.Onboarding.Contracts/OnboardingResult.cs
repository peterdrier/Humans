namespace Humans.Onboarding.Contracts;

/// <summary>
/// Result of an onboarding-funnel mutation: <c>Success</c> plus a caller-mapped
/// <c>ErrorKey</c> for the failure the caller is expected to render.
/// </summary>
/// <remarks>
/// On the leaf rather than inside the section because four services in
/// <c>Humans.Application</c> return it — <c>IUserService.ApplyProfileOnboardingMutationAsync</c>,
/// <c>IHumanLifecycleService</c>'s three lifecycle transitions,
/// <c>IAccountDeletionService.CancelDeletionAsync</c>/<c>PurgeAsync</c> and
/// <c>IRoleAssignmentService.AssignRoleAsync</c>/<c>EndRoleAsync</c>. Three of those four
/// are Onboarding's own siblings in the three-concerns split
/// (nobodies-collective/Humans#583, #584) that stayed in Base, so this really is the
/// funnel's vocabulary rather than a Base result type wearing the section's name.
/// </remarks>
public record OnboardingResult(bool Success, string? ErrorKey = null);
