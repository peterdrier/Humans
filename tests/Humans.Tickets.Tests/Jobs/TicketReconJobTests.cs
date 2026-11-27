using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Jobs;
using Humans.Tickets.Services.Checkout;
using Humans.Tickets.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Tickets.Tests.Jobs;

public sealed class TicketReconJobTests
{
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 10, 3, 30);
    private static readonly Instant Opened = Instant.FromUtc(2026, 12, 1, 9, 0);
    private readonly ITicketCheckoutService _checkout = Substitute.For<ITicketCheckoutService>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly List<EmailMessage> _sent = [];

    public TicketReconJobTests()
    {
        _checkout.ListTicketingSettingsAsync(Arg.Any<CancellationToken>()).Returns([new TicketingSettings { CreatedAt = Opened }]);
        _checkout.GetPlatformsInUseAsync(Arg.Any<CancellationToken>()).Returns(["boxoffice", "pretix"]);
        _checkout.ReconcilePlatformAsync(Arg.Any<string>(), Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(ci => Balanced(ci.ArgAt<string>(0)));
        _email.SendAsync(Arg.Do<EmailMessage>(_sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private static TicketRecon Balanced(string key)
    {
        var totals = new TicketReconTotals(0, 0, 0, 0, 0);
        return new TicketRecon(key, totals, totals, 0, [], [], null);
    }

    private TicketReconJob Job()
    {
        var zone = Substitute.For<IOrgTimeZone>();
        zone.Zone.Returns(DateTimeZoneProviders.Tzdb["Europe/Madrid"]);
        return new TicketReconJob(_checkout, _email, TestTicketsEmails.Create(), zone, new FakeClock(Now),
            NullLogger<TicketReconJob>.Instance);
    }

    [HumansFact]
    public async Task Sends_nothing_when_everything_balances()
    {
        await Job().ExecuteAsync(TestContext.Current.CancellationToken);

        _sent.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Mails_the_team_once_with_every_finding()
    {
        var order = new TicketCheckoutOrder { Id = Guid.NewGuid(), PlatformKey = "boxoffice", State = TicketCheckoutOrderState.Paid, TotalMinor = 30000, FeeMinor = 450 };
        var fee = new PaymentLedgerEntry("txn_1", Now, PaymentLedgerEntryKind.Charge, 30000, 470, order.Id, null);
        _checkout.ReconcilePaymentsAsync("boxoffice", Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(TicketPaymentReconBuilder.Build("boxoffice", Opened, Now, new PaymentLedger([fee], []), [order]));
        _checkout.ReconcilePlatformAsync("pretix", Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("boom"));

        await Job().ExecuteAsync(TestContext.Current.CancellationToken);

        var mail = _sent.Should().ContainSingle().Which;
        mail.RecipientEmail.Should().Be(TicketConstants.TicketsTeamEmail);
        mail.TemplateName.Should().Be("ticket_reconciliation_alert");
        mail.HtmlBody.Should().Contain("boxoffice payments and payouts: 1 movement(s) differ: fee differs from Humans&#39; 1")
            .And.Contain("pretix orders: could not be checked: boom")
            .And.Contain("/Tickets/Admin/Sales/Recon?from=2026-12-01&amp;to=2027-01-09");
    }

    [HumansFact]
    public async Task Reconciles_payments_over_the_whole_sale_up_to_today()
    {
        await Job().ExecuteAsync(TestContext.Current.CancellationToken);

        await _checkout.Received(1).ReconcilePaymentsAsync("boxoffice", Opened, Instant.FromUtc(2027, 1, 9, 23, 0),
            Arg.Any<CancellationToken>());
    }
}
