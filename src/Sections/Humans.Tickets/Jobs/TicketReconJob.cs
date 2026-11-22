using Hangfire;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Tickets.Services;
using Humans.Tickets.Services.Checkout;
using NodaTime;
using NodaTime.Text;

namespace Humans.Tickets.Jobs;

/// <summary>
/// The standing financial check: every backend in use, reconciled over the whole sale up to the start of today, so a
/// refund made late on an old order is caught too: its orders against the backend's list, and its payment account's
/// fees and payouts where Humans can read them. Changes nothing. Whatever is out of balance or could not be checked is
/// logged and mailed to the ticketing team in one message, every night until it balances.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 1800)]
internal sealed class TicketReconJob(
    ITicketCheckoutService checkout,
    IEmailService email,
    TicketsEmails emails,
    IOrgTimeZone orgTimeZone,
    IClock clock,
    ILogger<TicketReconJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var salesEvents = await checkout.GetSalesEventsAsync(cancellationToken);
        if (salesEvents.Count == 0)
            return;
        var zone = orgTimeZone.Zone;
        // No order is older than the sales setup it was sold under.
        var from = salesEvents.Min(e => e.CreatedAt);
        var today = clock.GetCurrentInstant().InZone(zone).Date;
        var to = today.AtStartOfDayInZone(zone).ToInstant();
        var findings = new List<string>();
        foreach (var key in await checkout.GetBackendsInUseAsync(cancellationToken))
        {
            if (await RunAsync(key, "orders", findings, () => checkout.ReconcileBackendAsync(key, from, to, cancellationToken),
                    cancellationToken) is { } recon && Finding(recon) is { } orderFinding)
                findings.Add(orderFinding);
            if (await RunAsync(key, "payments and payouts", findings,
                    () => checkout.ReconcilePaymentsAsync(key, from, to, cancellationToken), cancellationToken) is { } payments
                && Finding(payments) is { } paymentFinding)
                findings.Add(paymentFinding);
        }

        if (findings.Count == 0)
            return;
        var url = $"/Tickets/Admin/Sales/Recon?from={LocalDatePattern.Iso.Format(from.InZone(zone).Date)}" +
                  $"&to={LocalDatePattern.Iso.Format(today.PlusDays(-1))}";
        try
        {
            await email.SendAsync(emails.ReconciliationAlert(findings, url), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Ticket reconciliation alert could not be queued");
        }
    }

    /// <summary>One backend failing must not leave the others unchecked; a failure is itself a finding.</summary>
    private async Task<T?> RunAsync<T>(string key, string what, List<string> findings, Func<Task<T?>> run, CancellationToken ct)
        where T : class
    {
        try
        {
            return await run();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Ticket reconciliation of {Backend} {What} could not run", key, what);
            findings.Add($"{key} {what}: could not be checked: {ex.Message}");
            return null;
        }
    }

    private string? Finding(TicketRecon recon)
    {
        var key = recon.BackendKey;
        if (recon.Error is not null)
        {
            logger.LogError("Ticket reconciliation of {Backend} could not run: {Error}", key, recon.Error);
            return $"{key} orders: could not be checked: {recon.Error}";
        }
        if (recon.Balanced)
        {
            logger.LogInformation(
                "Ticket reconciliation of {Backend} is balanced: {Orders} paid orders, {Tickets} tickets, {Net} net; {Listed} unpaid difference(s) listed",
                key, recon.Humans.Orders, recon.Humans.Tickets, recon.Humans.NetMinor, recon.Problems.Count());
            return null;
        }

        var differing = recon.Problems.Where(r => r.MoneyMoved).ToList();
        var issues = TicketReconLabels.Counted(differing.SelectMany(r => r.Issues).Select(i => i.Label()));
        logger.LogError(
            "Ticket reconciliation of {Backend} is out of balance: Humans {HumansOrders} paid orders, {HumansTickets} tickets, " +
            "{HumansNet} net; backend {BackendOrders}, {BackendTickets}, {BackendNet}; {TicketsIssued} tickets issued; " +
            "{Problems} order(s) differ: {Issues}",
            key, recon.Humans.Orders, recon.Humans.Tickets, recon.Humans.NetMinor,
            recon.Backend.Orders, recon.Backend.Tickets, recon.Backend.NetMinor, recon.TicketsIssued, differing.Count, issues);
        return $"{key} orders: out of balance; Humans {recon.Humans.Orders} paid orders, {recon.Humans.Tickets} tickets, " +
               $"{TicketReconLabels.Eur(recon.Humans.NetMinor)} net; backend {recon.Backend.Orders}, {recon.Backend.Tickets}, " +
               $"{TicketReconLabels.Eur(recon.Backend.NetMinor)} net; {recon.TicketsIssued} tickets issued; " +
               $"{differing.Count} order(s) differ{(differing.Count == 0 ? "" : $": {issues}")}";
    }

    private string? Finding(TicketPaymentRecon recon)
    {
        var key = recon.BackendKey;
        if (recon.Error is not null)
        {
            logger.LogError("Payment reconciliation of {Backend} could not run: {Error}", key, recon.Error);
            return $"{key} payments and payouts: could not be checked: {recon.Error}";
        }
        if (recon.Balanced)
        {
            logger.LogInformation(
                "Payment reconciliation of {Backend} is balanced: {Charged} charged, {Fees} fees, {PaidOut} paid out",
                key, recon.Totals.ChargedMinor, recon.Totals.FeesMinor, recon.Totals.PaidOutMinor);
            return null;
        }

        var problems = recon.Problems.ToList();
        var payouts = recon.PayoutProblems.ToList();
        var parts = new List<string>();
        if (problems.Count > 0)
            parts.Add($"{problems.Count} movement(s) differ: {TicketReconLabels.Counted(problems.SelectMany(r => r.Issues).Select(i => i.Label()))}");
        if (payouts.Count > 0)
            parts.Add($"{payouts.Count} payout(s) differ: {TicketReconLabels.Counted(payouts.SelectMany(p => p.Issues).Select(i => i.Label()))}");
        var text = string.Join("; ", parts);
        logger.LogError("Payment reconciliation of {Backend} is out of balance: {Differences}", key, text);
        return $"{key} payments and payouts: {text}";
    }
}
