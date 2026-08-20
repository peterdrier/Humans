namespace Humans.Base.Attributes;

/// <summary>
/// Marks a service/interface method that reaches an outbound call which
/// <b>mutates state in a third-party system</b> (Google Workspace, TicketTailor,
/// Holded, MailerLite, Stripe, an SMTP relay …), whether directly or through
/// methods it calls.
/// </summary>
/// <remarks>
/// Consumed by <c>RequestScopedCancellationOnExternalWriteAnalyzer</c> (HUM0033),
/// which fails the build when a state-changing controller action
/// (<c>[HttpPost]</c> / <c>[HttpPut]</c> / <c>[HttpDelete]</c> / <c>[HttpPatch]</c>)
/// passes a <b>request-scoped</b> cancellation token — <c>HttpContext.RequestAborted</c>
/// or the action's own <c>CancellationToken</c> parameter — into a marked method.
///
/// <para>
/// Rationale and the full three-way token taxonomy live in
/// <c>memory/architecture/cancellation-token-propagation.md</c>. In short: a user
/// closing the tab must not tear a remote write in half, because a partially
/// applied vendor mutation has no rollback and no record of where it stopped.
/// </para>
///
/// <para>
/// <b>Apply it whenever you add a service method that reaches a new outbound
/// write.</b> The analyzer is only as complete as these markers — an unmarked
/// method is simply not checked. Mark the method on the <i>interface</i>: the
/// controller's compilation only ever sees the interface symbol, never the
/// Infrastructure implementation.
/// </para>
///
/// <para>
/// A method that is write-<i>capable</i> is marked even when some arguments make
/// a particular call read-only (e.g. a <c>SyncAction.Preview</c> reconcile). The
/// HTTP verb of the calling action is what decides: a <c>[HttpGet]</c> action may
/// still pass a request-scoped token, a state-changing one may not.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Method,
    AllowMultiple = false,
    Inherited = true)]
public sealed class ExternalWriteAttribute : Attribute;
