namespace Humans.Budget.Contracts;

/// <summary>
/// The seam Shell's <c>/dev/seed/budget</c> action uses to fill a non-production
/// environment with demo budget data. One method, because that is what the caller does —
/// exposing the section's fifteen write methods on the contracts leaf to serve a dev
/// button is what design §15.6b warns against.
/// </summary>
public interface IBudgetDemoSeeder
{
    /// <summary>
    /// Seeds demo teams, groups, categories and line items under a demo budget year.
    /// Returns the operator-facing success message.
    /// </summary>
    Task<string> SeedAsync(Guid actorUserId, CancellationToken cancellationToken = default);
}
