
namespace Humans.Users.Contracts;

public interface INonCompliantMemberSuspension
{
    /// <summary>
    /// Suspends members missing required consents past the grace period, with each suspension's side effects.
    /// Best-effort per user: an email, notification or Google failure is logged and the sweep goes on.
    /// </summary>
    Task SuspendNonCompliantAsync(CancellationToken cancellationToken = default);
}
