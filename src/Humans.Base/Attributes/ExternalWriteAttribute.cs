namespace Humans.Base.Attributes;

/// <summary>
/// Marks a service/interface method that reaches an outbound call which
/// <b>mutates state in a third-party system</b> (Google Workspace, TicketTailor,
/// Holded, MailerLite, Stripe, an SMTP relay …), whether directly or through
/// methods it calls.
/// </summary>
/// <remarks>
/// <para>
/// HUM0033 fails the build when a state-changing controller action passes a request-scoped cancellation token
/// (<c>HttpContext.RequestAborted</c> or the action's own token) into a marked method: a closed tab must not tear a
/// remote write in half, since a partial vendor mutation has no rollback
/// (<c>memory/architecture/cancellation-token-propagation.md</c>).
/// </para>
/// <para>
/// Mark every method that reaches a new outbound write, on the <i>interface</i>: an unmarked method is not checked,
/// and the controller only sees the interface symbol. A write-<i>capable</i> method is marked even when some
/// arguments make a call read-only; the calling action's HTTP verb decides.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Method,
    AllowMultiple = false,
    Inherited = true)]
public sealed class ExternalWriteAttribute : Attribute;
