using AwesomeAssertions;
using Humans.Tickets.Controllers;
using Humans.Tickets.Domain.Checkout;
using NodaTime;
using Xunit;

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

    public enum NumericField { Vat, Wave, Vip, Rule, Child }

    [HumansTheory]
    [InlineData(NumericField.Vat, "1,23", false)]
    [InlineData(NumericField.Vat, "1,000.25", false)]
    [InlineData(NumericField.Vat, "1.23e1", true)]
    [InlineData(NumericField.Wave, "1,23", false)]
    [InlineData(NumericField.Wave, "1,000.25", false)]
    [InlineData(NumericField.Wave, "1.23e1", true)]
    [InlineData(NumericField.Vip, "1,23", false)]
    [InlineData(NumericField.Vip, "1,000.25", false)]
    [InlineData(NumericField.Vip, "1.23e1", true)]
    [InlineData(NumericField.Rule, "1,23", false)]
    [InlineData(NumericField.Rule, "1,000.25", false)]
    [InlineData(NumericField.Rule, "1.23e1", true)]
    [InlineData(NumericField.Child, "1,23", false)]
    [InlineData(NumericField.Child, "1,000.25", false)]
    [InlineData(NumericField.Child, "1.23e1", true)]
    public void ToSave_HtmlNumber_ValidatesGrammar(NumericField field, string text, bool valid)
    {
        var form = new SalesEditForm();
        switch (field)
        {
            case NumericField.Vat: form.TicketVatPercent = text; break;
            case NumericField.Wave: form.Waves = [new() { Name = "Wave", EndsOn = "2027-01-31", Price = text }]; break;
            case NumericField.Vip: form.VipLevels = [new() { Total = text }]; break;
            case NumericField.Rule:
                form.PriceRules = [new() { GrantProgramId = LowIncome, Kind = TicketPriceRuleKind.ClassPrice, Label = "Class", Amount = text }];
                break;
            case NumericField.Child: form.ChildTypes = [new() { Name = "Child", UnderAge = 12, Price = text }]; break;
        }

        var (save, error) = form.ToSave(null, Madrid);

        if (!valid)
        {
            save.Should().BeNull();
            error.Should().NotBeNullOrEmpty();
            return;
        }
        error.Should().BeNull();
        save.Should().NotBeNull();
        if (field == NumericField.Vat) save!.TicketVatRate.Should().Be(0.123m);
        else
        {
            var minor = field switch
            {
                NumericField.Wave => save!.Waves.Single().PriceMinor,
                NumericField.Vip => save!.VipTotalsMinor.Single(),
                NumericField.Rule => save!.PriceRules.Single().AmountMinor,
                _ => save!.ChildTypes.Single().PriceMinor
            };
            minor.Should().Be(1230L);
        }
    }

    [HumansFact]
    public void A_half_filled_row_is_refused_by_name()
    {
        var form = new SalesEditForm { Waves = [new() { Name = "Wave 2", Price = "250" }] };

        form.ToSave(null, Madrid).Error.Should().Be("Wave 'Wave 2' needs a last day.");
    }

    [HumansTheory]
    [InlineData("wave")]
    [InlineData("vip")]
    [InlineData("rule")]
    [InlineData("child")]
    public void Unrepresentable_amounts_are_refused_in_every_amount_field(string field)
    {
        foreach (var amount in new[] { "92233720368547758.08", "79228162514264337593543950335" })
        {
            var form = new SalesEditForm
            {
                Waves = string.Equals(field, "wave", StringComparison.Ordinal) ? [new() { Name = "Wave", EndsOn = "2027-01-31", Price = amount }] : [],
                VipLevels = string.Equals(field, "vip", StringComparison.Ordinal) ? [new() { Total = amount }] : [],
                PriceRules = string.Equals(field, "rule", StringComparison.Ordinal)
                    ? [new() { GrantProgramId = LowIncome, Kind = TicketPriceRuleKind.ClassPrice, Label = "Class", Amount = amount }] : [],
                ChildTypes = string.Equals(field, "child", StringComparison.Ordinal) ? [new() { Name = "Child", UnderAge = 12, Price = amount }] : [],
            };

            var (save, error) = form.ToSave(null, Madrid);

            save.Should().BeNull();
            error.Should().NotBeNullOrEmpty();
        }
    }

    [HumansTheory]
    [InlineData("92233720368547758.07", long.MaxValue)]
    [InlineData("92233720368547758.074", long.MaxValue)]
    [InlineData("1.005", 101L)]
    public void Representable_prices_keep_away_from_zero_cent_rounding(string amount, long expected)
    {
        var form = new SalesEditForm
        {
            Waves = [new() { Name = "Wave", EndsOn = "2027-01-31", Price = amount }],
        };

        var (save, error) = form.ToSave(null, Madrid);

        error.Should().BeNull();
        save!.Waves.Single().PriceMinor.Should().Be(expected);
    }
}
