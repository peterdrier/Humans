using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Repositories.Tickets;

/// <summary>
/// The JSON-owned lists (order lines, waves, price rules) must survive an update of a detached copy, which is how the
/// service hands them back. The in-memory fakes cannot catch EF's shadow-key failure here.
/// </summary>
public class TicketCheckoutRepositoryTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static readonly Instant Now = Instant.FromUtc(2027, 1, 10, 12, 0);

    [HumansFact]
    public async Task Detached_order_and_ticketing_settings_round_trip_their_json_lists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITicketCheckoutRepository>();
        var holder = Guid.NewGuid();

        var ticketing = new TicketingSettings
        {
            Id = Guid.NewGuid(),
            EventSettingsId = Guid.NewGuid(),
            PlatformKey = "boxoffice",
            Capacity = 10,
            Waves = [new TicketSalesWave { Name = "Wave 1", EndsOn = new LocalDate(2027, 1, 31), PriceMinor = 22500 }],
            VipTotalsMinor = [40000],
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await repo.SaveTicketingSettingsAsync(ticketing, ct);
        var loadedSettings = (await repo.GetTicketingSettingsAsync(ticketing.Id, ct))!;
        loadedSettings.Waves.Add(new TicketSalesWave { Name = "Wave 2", EndsOn = new LocalDate(2027, 2, 28), PriceMinor = 25000 });
        loadedSettings.PriceRules.Add(new TicketPriceRule { GrantProgramId = Guid.NewGuid(), Kind = TicketPriceRuleKind.Discount, AmountMinor = 5000, Label = "Discount" });
        loadedSettings.Switches.Should().BeNull();
        loadedSettings.Switches = new TicketingSwitches { Resale = ResaleScheme.Queue, Donations = false };
        await repo.SaveTicketingSettingsAsync(loadedSettings, ct);
        var switches = (await repo.GetTicketingSettingsAsync(ticketing.Id, ct))!.Switches;
        switches.Should().BeEquivalentTo(new TicketingSwitches { Resale = ResaleScheme.Queue, Donations = false });
        switches!.CompanionTicketsOn.Should().BeTrue("a switch saved before companion tickets had one means on");

        // Ticketing settings saved before child types existed have no child_types JSON at all.
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TicketsDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(ct))
            await db.Database.ExecuteSqlAsync($"UPDATE ticket_sales_events SET child_types = NULL WHERE \"Id\" = {ticketing.Id}", ct);
        loadedSettings = (await repo.GetTicketingSettingsAsync(ticketing.Id, ct))!;
        loadedSettings.ChildTypes.Should().BeEmpty();
        loadedSettings.DonationPurposes.Should().BeNull();
        loadedSettings.ChildTypes = [new TicketChildType { Name = "Under 13", UnderAge = 13, PriceMinor = 5000 }];
        loadedSettings.DonationPurposes = ["Art"];
        await repo.SaveTicketingSettingsAsync(loadedSettings, ct);
        var typed = (await repo.GetTicketingSettingsAsync(ticketing.Id, ct))!;
        typed.ChildTypes.Should().BeEquivalentTo([new TicketChildType { Name = "Under 13", UnderAge = 13, PriceMinor = 5000 }]);
        typed.DonationPurposes.Should().Equal("Art");

        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = ticketing.Id,
            BuyerUserId = holder,
            BuyerName = "Ana",
            BuyerEmail = "ana@example.com",
            PlatformKey = "boxoffice",
            TotalMinor = 22500,
            CreatedAt = Now,
            ExpiresAt = Now + Duration.FromHours(1),
            UpdatedAt = Now,
            Lines = [new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, Description = "Ticket", AmountMinor = 22500, HolderUserId = holder }],
        };
        await repo.AddOrderAsync(order, ct);
        (await repo.TryClaimPaidAsync(order.Id, Now, ct)).Should().BeTrue();
        (await repo.TryClaimPaidAsync(order.Id, Now, ct)).Should().BeFalse();
        var loaded = (await repo.GetOrderAsync(order.Id, ct))!;
        loaded.State.Should().Be(TicketCheckoutOrderState.Paid);
        loaded.PlatformOrderId = "cs_1";
        loaded.Lines[0].WaveName = "Wave 1";
        await repo.UpdateOrderAsync(loaded, ct);
        await repo.UpdateOrderAsync(loaded, ct);

        await repo.ErasePiiForUserAsync(holder, ["ana@example.com"], ct);
        await repo.ReassignAsync(holder, Guid.NewGuid(), ct);

        var stored = (await repo.GetOrderAsync(order.Id, ct))!;
        stored.PlatformOrderId.Should().Be("cs_1");
        stored.State.Should().Be(TicketCheckoutOrderState.Paid);
        stored.Lines.Should().ContainSingle().Which.WaveName.Should().Be("Wave 1");
        stored.Lines[0].HolderUserId.Should().NotBe(holder);
        stored.BuyerName.Should().NotBe("Ana");
        var storedSettings = (await repo.GetTicketingSettingsAsync(ticketing.Id, ct))!;
        storedSettings.Waves.Select(w => w.Name).Should().Equal("Wave 1", "Wave 2");
        storedSettings.PriceRules.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Renamed_members_keep_their_stored_names_so_older_rows_still_read()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITicketCheckoutRepository>();
        var ticketing = new TicketingSettings
        {
            Id = Guid.NewGuid(),
            EventSettingsId = Guid.NewGuid(),
            PlatformKey = "boxoffice",
            PriceRules = [new TicketPriceRule { GrantProgramId = Guid.NewGuid(), Kind = TicketPriceRuleKind.Discount, AmountMinor = 5000 }],
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await repo.SaveTicketingSettingsAsync(ticketing, ct);
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = ticketing.Id,
            BuyerUserId = Guid.NewGuid(),
            BuyerName = "Ana",
            BuyerEmail = "ana@example.com",
            PlatformKey = "boxoffice",
            TotalMinor = 17500,
            CreatedAt = Now,
            ExpiresAt = Now + Duration.FromHours(1),
            UpdatedAt = Now,
            Lines = [new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, AmountMinor = 17500, HolderName = "Ana", DiscountsMinor = 5000 }],
        };
        await repo.AddOrderAsync(order, ct);
        await repo.AddOrderEventAsync(new TicketCheckoutOrderEvent
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            At = Now,
            Kind = TicketCheckoutOrderEventKind.PlatformRefused,
            Detail = "no",
        }, ct);

        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TicketsDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            (await db.Database.SqlQuery<string>($"SELECT price_rules::text AS \"Value\" FROM ticket_sales_events WHERE \"Id\" = {ticketing.Id}").SingleAsync(ct))
                .Should().Contain("\"Credit\"");
            (await db.Database.SqlQuery<string>($"SELECT lines::text AS \"Value\" FROM ticket_checkout_orders WHERE \"Id\" = {order.Id}").SingleAsync(ct))
                .Should().Contain("\"AttendeeName\"").And.Contain("\"CreditsMinor\"");
            (await db.Database.SqlQuery<string>($"SELECT \"Kind\" AS \"Value\" FROM ticket_checkout_order_events WHERE \"OrderId\" = {order.Id}").SingleAsync(ct))
                .Should().Be("BackendRefused");
        }

        (await repo.GetTicketingSettingsAsync(ticketing.Id, ct))!.PriceRules.Single().Kind.Should().Be(TicketPriceRuleKind.Discount);
        var line = (await repo.GetOrderAsync(order.Id, ct))!.Lines.Single();
        line.HolderName.Should().Be("Ana");
        line.DiscountsMinor.Should().Be(5000);
        (await repo.GetOrderEventsAsync(order.Id, ct)).Single().Kind.Should().Be(TicketCheckoutOrderEventKind.PlatformRefused);
    }

    [HumansFact]
    public async Task Tickets_and_offers_round_trip_and_erasure_clears_their_personal_data()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITicketCheckoutRepository>();
        var holder = Guid.NewGuid();
        var settingsId = Guid.NewGuid();
        var adult = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = settingsId,
            OrderId = Guid.NewGuid(),
            HolderUserId = holder,
            HolderName = "Ana García",
            HolderEmail = "ana@example.com",
            Barcode = "H" + Guid.NewGuid().ToString("N")[..16],
            PricePaidMinor = 22500,
            WaveName = "Wave 1",
            IssuedAt = Now,
        };
        var child = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = settingsId,
            OrderId = adult.OrderId,
            Kind = TicketHolderKind.Child,
            ParentTicketId = adult.Id,
            LineIndex = 1,
            HolderName = "Kid García",
            Barcode = "H" + Guid.NewGuid().ToString("N")[..16],
            WaveName = "Child",
            IssuedAt = Now,
        };
        (await repo.AddTicketsAsync([adult, child], ct)).Should().BeTrue();
        (await repo.AddTicketsAsync([new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(), TicketingSettingsId = settingsId, OrderId = adult.OrderId, HolderName = "Twice",
            Barcode = "H" + Guid.NewGuid().ToString("N")[..16], IssuedAt = Now,
        }], ct)).Should().BeFalse();
        var invitedElsewhere = new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = settingsId,
            TicketId = Guid.NewGuid(),
            HolderUserId = Guid.NewGuid(),
            OfferedAt = Now,
            Invites =
            [
                new TicketResaleInvite { Email = "Ana@Example.com", TokenHash = "DEF", SentAt = Now },
                new TicketResaleInvite { Email = "cy@example.com", TokenHash = "GHI", SentAt = Now },
            ],
        };
        await repo.AddOfferAsync(invitedElsewhere, ct);
        var unattached = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = settingsId,
            OrderId = Guid.NewGuid(),
            HolderName = "Ana García",
            HolderEmail = "ANA@example.com",
            Barcode = "H" + Guid.NewGuid().ToString("N")[..16],
            IssuedAt = Now,
        };
        await repo.AddTicketsAsync([unattached], ct);
        var offer = new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = settingsId,
            TicketId = adult.Id,
            HolderUserId = holder,
            OfferedAt = Now,
            RefundOwedMinor = 22500,
            Invites = [new TicketResaleInvite { Email = "bo@example.com", TokenHash = "ABC", SentAt = Now }],
        };
        await repo.AddOfferAsync(offer, ct);
        adult.State = TicketCheckoutTicketState.Offered;
        await repo.UpdateTicketAsync(adult, ct);

        (await repo.GetTicketByBarcodeAsync(adult.Barcode, ct))!.State.Should().Be(TicketCheckoutTicketState.Offered);
        (await repo.GetTicketsForOrderAsync(adult.OrderId, ct)).Should().HaveCount(2);
        (await repo.GetOfferByTokenHashAsync("ABC", ct))!.Id.Should().Be(offer.Id);
        (await repo.GetOfferByTokenHashAsync("GHI", ct))!.Id.Should().Be(invitedElsewhere.Id);

        await repo.ErasePiiForUserAsync(holder, ["ana@example.com"], ct);

        (await repo.GetTicketAsync(child.Id, ct))!.HolderName.Should().NotBe("Kid García");
        var storedElsewhere = (await repo.GetOfferAsync(invitedElsewhere.Id, ct))!;
        storedElsewhere.Invites.Select(i => i.Email).Should().Equal("cy@example.com");
        (await repo.GetTicketAsync(unattached.Id, ct))!.HolderName.Should().NotBe("Ana García");
        var stored = (await repo.GetTicketAsync(adult.Id, ct))!;
        stored.HolderName.Should().NotBe("Ana García");
        stored.HolderEmail.Should().NotBe("ana@example.com");
        stored.Barcode.Should().Be(adult.Barcode);
        var storedOffer = (await repo.GetOfferAsync(offer.Id, ct))!;
        storedOffer.Invites.Should().BeEmpty();
        storedOffer.RefundOwedMinor.Should().Be(22500);
    }

    [HumansFact]
    public async Task An_offer_with_several_invites_updates_twice_and_keeps_its_list()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITicketCheckoutRepository>();
        var offer = new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = Guid.NewGuid(),
            TicketId = Guid.NewGuid(),
            HolderUserId = Guid.NewGuid(),
            OfferedAt = Now,
            Invites =
            [
                new TicketResaleInvite { Email = "a@example.com", TokenHash = "A", SentAt = Now },
                new TicketResaleInvite { Email = "b@example.com", TokenHash = "B", SentAt = Now },
            ],
        };
        await repo.AddOfferAsync(offer, ct);

        var loaded = (await repo.GetOfferAsync(offer.Id, ct))!;
        loaded.Invites = [.. loaded.Invites, new TicketResaleInvite { Email = "c@example.com", TokenHash = "C", SentAt = Now }];
        await repo.UpdateOfferAsync(loaded, ct);
        var again = (await repo.GetOfferAsync(offer.Id, ct))!;
        again.State = TicketResaleOfferState.Sold;
        again.RefundId = Guid.NewGuid();
        await repo.UpdateOfferAsync(again, ct);

        var stored = (await repo.GetOfferAsync(offer.Id, ct))!;
        stored.Invites.Select(i => i.TokenHash).Should().Equal("A", "B", "C");
        stored.State.Should().Be(TicketResaleOfferState.Sold);
        stored.RefundId.Should().Be(again.RefundId);
    }

    [HumansFact]
    public async Task Erasure_clears_a_companion_on_an_order_of_its_own()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITicketCheckoutRepository>();
        var holder = Guid.NewGuid();
        var settingsId = Guid.NewGuid();
        var adult = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = settingsId,
            OrderId = Guid.NewGuid(),
            HolderUserId = holder,
            HolderName = "Ana García",
            Barcode = "H" + Guid.NewGuid().ToString("N")[..16],
            IssuedAt = Now,
        };
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = settingsId,
            BuyerUserId = holder,
            BuyerName = "Ana García",
            BuyerEmail = "ana@example.com",
            PlatformKey = "boxoffice",
            CreatedAt = Now,
            ExpiresAt = Now,
            UpdatedAt = Now,
            Lines = [new TicketCheckoutOrderLine
            {
                Kind = OrderLineKind.Ticket, HolderKind = TicketHolderKind.Companion, Description = "Companion ticket", HolderName = "Helper Ruiz",
            }],
        };
        await repo.AddOrderAsync(order, ct);
        var companion = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = settingsId,
            OrderId = order.Id,
            Kind = TicketHolderKind.Companion,
            ParentTicketId = adult.Id,
            HolderName = "Helper Ruiz",
            Barcode = "H" + Guid.NewGuid().ToString("N")[..16],
            IssuedAt = Now,
        };
        (await repo.AddTicketsAsync([adult, companion], ct)).Should().BeTrue();

        await repo.ErasePiiForUserAsync(holder, ["ana@example.com"], ct);

        (await repo.GetOrderAsync(order.Id, ct))!.Lines.Should().ContainSingle().Which.HolderName.Should().NotBe("Helper Ruiz");
        (await repo.GetTicketAsync(companion.Id, ct))!.HolderName.Should().NotBe("Helper Ruiz");
    }

    [HumansFact]
    public async Task Companion_requests_round_trip_and_erasure_of_either_person_clears_the_notes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITicketCheckoutRepository>();
        var holder = Guid.NewGuid();
        var companion = Guid.NewGuid();
        var request = new TicketCompanionRequest
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = Guid.NewGuid(),
            TicketId = Guid.NewGuid(),
            HolderUserId = holder,
            CompanionUserId = companion,
            Note = "Ana uses a wheelchair",
            RequestedAt = Now,
        };
        await repo.AddCompanionRequestAsync(request, ct);
        var loaded = (await repo.GetCompanionRequestAsync(request.Id, ct))!;
        loaded.State = TicketCompanionRequestState.Declined;
        loaded.DecisionNote = "Ana already has a carer ticket";
        loaded.DecidedAt = Now;
        await repo.UpdateCompanionRequestAsync(loaded, ct);
        (await repo.GetCompanionRequestsForUserAsync(companion, ct)).Should().ContainSingle()
            .Which.State.Should().Be(TicketCompanionRequestState.Declined);

        await repo.ErasePiiForUserAsync(companion, [], ct);

        var erased = (await repo.GetCompanionRequestAsync(request.Id, ct))!;
        erased.Note.Should().NotContain("Ana");
        erased.DecisionNote.Should().BeNull();
    }
}
