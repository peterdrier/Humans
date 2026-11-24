using AwesomeAssertions;
using Humans.Tickets.Controllers;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Tests.Controllers;

public sealed class SalesEditFormTests
{
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private static readonly Guid LowIncome = Guid.NewGuid();

    [HumansFact]
    public void Rows_become_waves_rules_and_vip_totals_and_blank_rows_are_dropped()
    {
        var form = new SalesEditForm
        {
            EventSettingsId = Guid.NewGuid(),
            PlatformKey = "boxoffice",
            TicketVatPercent = "21",
            SalesCloseAt = "2027-05-25T23:59",
            Waves =
            [
                new() { Name = "Wave 1", EndsOn = "2027-01-31", Price = "225" },
                new(),
            ],
            VipLevels = [new() { Total = "400" }, new() { Total = " " }],
            PriceRules =
            [
                new() { GrantProgramId = LowIncome, Kind = TicketPriceRuleKind.WavePin, Amount = null, Label = "Low income" },
                new() { GrantProgramId = LowIncome, Kind = TicketPriceRuleKind.ClassPrice, Amount = "125.50", Label = "Low income" },
                new() { GrantProgramId = LowIncome, Kind = TicketPriceRuleKind.EarlyAccess, Amount = "", Label = "Testers" },
            ],
        };

        var (save, error) = form.ToSave(null, Madrid);

        error.Should().BeNull();
        save!.TicketVatRate.Should().Be(0.21m);
        save.SalesCloseAt.Should().Be(Instant.FromUtc(2027, 5, 25, 21, 59));
        save.Waves.Should().ContainSingle().Which.PriceMinor.Should().Be(22500);
        save.VipTotalsMinor.Should().Equal(40000);
        save.PriceRules.Select(r => (r.Kind, r.AmountMinor)).Should().Equal(
            (TicketPriceRuleKind.WavePin, 0L), (TicketPriceRuleKind.ClassPrice, 12550L), (TicketPriceRuleKind.EarlyAccess, 0L));
    }

    [HumansFact]
    public void Saved_ticketing_settings_round_trip_through_the_form()
    {
        var ticketing = new TicketingSettings
        {
            EventSettingsId = Guid.NewGuid(),
            PlatformKey = "pretix",
            TicketVatRate = 0.10m,
            SalesCloseAt = Instant.FromUtc(2027, 5, 25, 21, 59),
            Waves = [new TicketSalesWave { Name = "Wave 1", EndsOn = new LocalDate(2027, 1, 31), PriceMinor = 22550 }],
            VipTotalsMinor = [100000],
            PriceRules = [new TicketPriceRule { GrantProgramId = LowIncome, Kind = TicketPriceRuleKind.Discount, AmountMinor = 5000, Label = "Discount" }],
        };

        var form = SalesEditForm.From(ticketing, Madrid);
        form.TicketVatPercent.Should().Be("10");
        form.SalesCloseAt.Should().Be("2027-05-25T23:59");

        var (save, _) = form.ToSave(null, Madrid);
        save!.TicketVatRate.Should().Be(0.10m);
        save.SalesCloseAt.Should().Be(ticketing.SalesCloseAt);
        save.Waves.Single().PriceMinor.Should().Be(22550);
        save.VipTotalsMinor.Should().Equal(100000);
        save.PriceRules.Single().AmountMinor.Should().Be(5000);
    }

    [HumansFact]
    public void A_half_filled_row_is_refused_by_name()
    {
        var form = new SalesEditForm { Waves = [new() { Name = "Wave 2", Price = "250" }] };

        form.ToSave(null, Madrid).Error.Should().Be("Wave 'Wave 2' needs a last day.");
    }
}
