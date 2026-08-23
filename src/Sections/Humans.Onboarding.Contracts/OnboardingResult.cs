namespace Humans.Onboarding.Contracts;

/// <summary>
/// Result of an onboarding-funnel mutation: <c>Success</c> plus a caller-mapped
/// <c>ErrorKey</c> for the failure the caller is expected to render.
/// </summary>
/// <remarks>
/// On the leaf rather than inside the section because Users returns it from three separate
/// surfaces — <c>IUserService.ApplyProfileOnboardingMutationAsync</c>,
/// <c>IHumanLifecycleService</c>'s three lifecycle transitions, and
/// <c>IAccountDeletionService.CancelDeletionAsync</c>/<c>PurgeAsync</c>. The last two are
/// Onboarding's own siblings in the three-concerns split (nobodies-collective/Humans#583,
/// #584), so this really is the funnel's vocabulary rather than a Base result type wearing
/// the section's name. (<c>IRoleAssignmentService</c> is <em>not</em> one of them: Auth's
/// assign/end pair returns its own <c>RoleAssignmentResult</c>.)
/// </remarks>
public record OnboardingResult(bool Success, string? ErrorKey = null);
