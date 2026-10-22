namespace Humans.Budget.Contracts;

/// <summary>
/// For Development's <c>/dev/seed/budget</c> only. One method on purpose: exposing the section's write methods to serve
/// a dev button is what the Contracts rule forbids.
/// </summary>
public interface IBudgetDemoSeeder
{
    /// <summary>Returns the operator-facing success message.</summary>
    Task<string> SeedAsync(Guid actorUserId, CancellationToken cancellationToken = default);
}
