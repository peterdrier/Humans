using Humans.Base.Interfaces;
using Humans.Email.Contracts;

namespace Humans.Email.Services;

/// <summary>
/// The outbox admin surface: retry, discard, pause/resume. Internal —
/// its only consumer is the section's own <c>EmailController</c>. The per-user history
/// reads and the dashboard stats it inherits from <see cref="IEmailOutboxServiceRead"/>
/// are the half that leaves, on the contracts leaf, for Shell's profile, user-admin
/// outbox pages, and the admin dashboard.
/// </summary>
/// <remarks>
/// The interface survives the internalise pass because <c>MA0053</c> seals the concrete
/// service and Castle DynamicProxy cannot substitute a sealed class (design §15 step 5,
/// Budget's rule) — <c>EmailOutboxServiceTests</c> and the controller tests stub it.
/// </remarks>
internal interface IEmailOutboxService : IEmailOutboxServiceRead, IEmailOutboxRetention, IApplicationService
{
    /// <summary>
    /// Requeues a failed or stuck email outbox message for retry.
    /// Returns the recipient email if found, or null if the message does not exist.
    /// </summary>
    Task<string?> RetryMessageAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards (deletes) an email outbox message.
    /// Returns the recipient email if found, or null if the message does not exist.
    /// </summary>
    Task<string?> DiscardMessageAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether email sending is currently paused.
    /// </summary>
    Task<bool> IsEmailPausedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the email sending paused state.
    /// </summary>
    Task SetEmailPausedAsync(bool paused, CancellationToken cancellationToken = default);
}
