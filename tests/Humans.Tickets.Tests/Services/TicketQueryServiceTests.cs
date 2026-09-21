using AwesomeAssertions;
using Humans.Budget.Contracts;
using Humans.Campaigns.Contracts;
using Humans.Users.Contracts;
using Humans.Tickets.Data;
using Humans.Settings.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Services;
using Humans.Base.Constants;
using Humans.Base.Enums;
using NodaTime;
using NSubstitute;
using Humans.Tickets.Domain;
using Humans.Tickets.Contracts;

namespace Humans.Tickets.Tests.Services;

public sealed class TicketQueryServiceTests : TicketsTestHarness
{
    private readonly TicketRepository _repo;
    private readonly ITicketTransferRepository _transferRepo = Substitute.For<ITicketTransferRepository>();
    private readonly IBudgetServiceRead _budgetService = Substitute.For<IBudgetServiceRead>();
    private readonly ICampaignServiceRead _campaignService = Substitute.For<ICampaignServiceRead>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly ISettingsService _shiftManagementService = Substitute.For<ISettingsService>();
    private readonly ITicketCacheInvalidator _cacheInvalidator = Substitute.For<ITicketCacheInvalidator>();
    private readonly TicketQueryService _service;

    public TicketQueryServiceTests()
    {
        _repo = new TicketRepository(TicketsDbFactory, Clock);

        _transferRepo.GetByStatusAsync(Arg.Any<TicketTransferStatus>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _service = new TicketQueryService(
            _repo,
            _transferRepo,
            _budgetService,
            _campaignService,
            _userService,
            _userEmailService,
            _teamService,
            _shiftManagementService,
            _cacheInvalidator,
            SystemClock.Instance);

        // Defaults for the Volunteers team lookup — tests that care override them.
        _teamService.GetTeamAsync(SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(VolunteersTeam([]));

        _userService.GetAllParticipationsForYearAsync(
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        StubUserInfos(_userService);

        _userEmailService.GetNotificationEmailsByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());

        _userEmailService.GetVerifiedEmailsForUserAsync(
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        _campaignService.GetCodeTrackingAsync(Arg.Any<CancellationToken>())
            .Returns(new CampaignCodeTrackingData([], []));
    }

    [HumansFact]
    public async Task EraseForUserAsync_scrubs_ticket_and_transfer_data_then_drops_the_warmed_projection()
    {
        var userId = Guid.NewGuid();

        await _service.EraseForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await _transferRepo.Received(1).ErasePiiForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);
        _cacheInvalidator.Received(1).InvalidateAll();
    }

    [HumansFact]
    public async Task ContributeForUserAsync_exports_only_the_users_matched_order_and_attendee_records()
    {
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await TicketsDb.TicketOrders.AddAsync(new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "export-order",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.org",
            TotalAmount = 123m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            DiscountCode = "CODE",
            VendorEventId = "event",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            MatchedUserId = userId
        }, Xunit.TestContext.Current.CancellationToken);
        await TicketsDb.TicketAttendees.AddRangeAsync(
            [
                new TicketAttendee
                {
                    Id = Guid.NewGuid(), VendorTicketId = "matched", TicketOrderId = orderId,
                    AttendeeName = "Holder", AttendeeEmail = "holder@example.org", TicketTypeName = "Full Week",
                    Price = 100m, Status = TicketAttendeeStatus.Valid, VendorEventId = "event",
                    SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0), MatchedUserId = userId
                },
                new TicketAttendee
                {
                    Id = Guid.NewGuid(), VendorTicketId = "other", TicketOrderId = orderId,
                    AttendeeName = "Other", AttendeeEmail = "other@example.org", TicketTypeName = "Full Week",
                    Price = 100m, Status = TicketAttendeeStatus.Valid, VendorEventId = "event",
                    SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0), MatchedUserId = Guid.NewGuid()
                }
            ], Xunit.TestContext.Current.CancellationToken);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var slices = await _service.ContributeForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        slices.Select(slice => slice.SectionName).Should().BeEquivalentTo(
            TicketQueryService.TicketOrders, TicketQueryService.TicketAttendeeMatches);
        var json = System.Text.Json.JsonSerializer.Serialize(slices);
        json.Should().Contain("buyer@example.org").And.Contain("holder@example.org").And.NotContain("other@example.org");
    }

    [HumansFact]
    public async Task GetSalesAggregatesAsync_ExcludesVoidTicketsFromCountsAndVipDonations()
    {
        var orderId = Guid.NewGuid();
        var order = new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_weekly",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 500m,
            DonationAmount = 25m,
            VatAmount = 28.64m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 5),
            Attendees =
            [
                new TicketAttendee
                {
                    Id = Guid.NewGuid(),
                    VendorTicketId = "tkt_valid_vip",
                    TicketOrderId = orderId,
                    TicketOrder = null!,
                    AttendeeName = "Valid VIP",
                    TicketTypeName = "VIP",
                    Price = 400m,
                    Status = TicketAttendeeStatus.Valid,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 5),
                },
                new TicketAttendee
                {
                    Id = Guid.NewGuid(),
                    VendorTicketId = "tkt_void_vip",
                    TicketOrderId = orderId,
                    TicketOrder = null!,
                    AttendeeName = "Void VIP",
                    TicketTypeName = "VIP",
                    Price = 500m,
                    Status = TicketAttendeeStatus.Void,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 5),
                }
            ]
        };

        TicketsDb.TicketOrders.Add(order);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetSalesAggregatesAsync();

        result.WeeklySales.Should().ContainSingle();
        result.QuarterlySales.Should().ContainSingle();

        var weekly = result.WeeklySales.Single();
        weekly.TicketsSold.Should().Be(1);
        weekly.Donations.Should().Be(25m);
        weekly.VipDonations.Should().Be(400m - TicketConstants.VipThresholdEuros);

        var quarterly = result.QuarterlySales.Single();
        quarterly.TicketsSold.Should().Be(1);
        quarterly.Donations.Should().Be(25m);
        quarterly.VipDonations.Should().Be(400m - TicketConstants.VipThresholdEuros);
    }

    [HumansFact]
    public async Task GetSalesAggregatesAsync_ExcludesRefundedAndCancelledOrders()
    {
        await TicketsDb.TicketOrders.AddRangeAsync(
            MakeOrder("ord_paid", TicketPaymentStatus.Paid, Instant.FromUtc(2026, 3, 2, 10, 0), 100m, 0m, 9.09m, 1, 0m),
            MakeOrder("ord_refunded", TicketPaymentStatus.Refunded, Instant.FromUtc(2026, 3, 2, 12, 0), 999m, 50m, 90m, 1, 200m),
            MakeOrder("ord_cancelled", TicketPaymentStatus.Cancelled, Instant.FromUtc(2026, 3, 3, 12, 0), 888m, 25m, 80m, 1, 100m));
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetSalesAggregatesAsync();

        result.WeeklySales.Should().ContainSingle();
        var weekly = result.WeeklySales.Single();
        weekly.OrderCount.Should().Be(1);
        weekly.GrossRevenue.Should().Be(100m);
        weekly.Donations.Should().Be(0m);
        weekly.VatAmount.Should().Be(9.09m);
        weekly.TicketsSold.Should().Be(1);
        weekly.VipDonations.Should().Be(0m);
    }

    [HumansFact]
    public async Task GetSalesAggregatesAsync_MonthlySales_SplitsPerMonthWithFeesAndRefundedGross()
    {
        // 400 seat + 25 standalone donation = 425 gross; VAT base is min(400, 315) = 315.
        var march = MakeOrder("ord_mar", TicketPaymentStatus.Paid, Instant.FromUtc(2026, 3, 2, 10, 0), 425m, 25m, 28.64m, 1, 85m);
        march.StripeFee = 5.50m;
        march.ApplicationFee = 2m;
        var april = MakeOrder("ord_apr", TicketPaymentStatus.Paid, Instant.FromUtc(2026, 4, 20, 10, 0), 315m, 0m, 28.64m, 1, 0m);
        april.StripeFee = 3m;
        april.ApplicationFee = 1m;
        var marchRefund = MakeOrder("ord_mar_refund", TicketPaymentStatus.Refunded, Instant.FromUtc(2026, 3, 9, 10, 0), 200m, 0m, 0m, 1, 0m);
        marchRefund.StripeFee = 4m;
        marchRefund.ApplicationFee = 1.50m;
        var mayRefund = MakeOrder("ord_may_refund", TicketPaymentStatus.Refunded, Instant.FromUtc(2026, 5, 1, 10, 0), 315m, 0m, 0m, 1, 0m);
        mayRefund.StripeFee = 6m;
        mayRefund.ApplicationFee = 2.50m;
        await TicketsDb.TicketOrders.AddRangeAsync(
            march,
            april,
            marchRefund,
            mayRefund,
            MakeOrder("ord_cancelled", TicketPaymentStatus.Cancelled, Instant.FromUtc(2026, 3, 3, 12, 0), 888m, 0m, 0m, 1, 0m));
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetSalesAggregatesAsync();

        result.MonthlySales.Select(m => m.MonthLabel).Should().Equal("2026-03", "2026-04", "2026-05");

        var mar = result.MonthlySales[0];
        mar.OrderCount.Should().Be(1);
        mar.TicketsSold.Should().Be(1);
        mar.GrossRevenue.Should().Be(425m);
        mar.Donations.Should().Be(25m);
        mar.VipDonations.Should().Be(85m);
        mar.VatAmount.Should().Be(28.64m);
        mar.TicketIncomeInclVat.Should().Be(315m);
        mar.TicketIncomeExVat.Should().Be(315m - 28.64m);
        // Refunds return the gross, not the fees already charged.
        mar.StripeFees.Should().Be(5.50m + 4m);
        mar.ApplicationFees.Should().Be(2m + 1.50m);
        mar.RefundedGross.Should().Be(200m);

        var apr = result.MonthlySales[1];
        apr.GrossRevenue.Should().Be(315m);
        apr.TicketIncomeInclVat.Should().Be(315m);
        apr.TicketIncomeExVat.Should().Be(315m - 28.64m);
        apr.StripeFees.Should().Be(3m);
        apr.RefundedGross.Should().Be(0m);

        var may = result.MonthlySales[2];
        may.OrderCount.Should().Be(0);
        may.GrossRevenue.Should().Be(0m);
        may.TicketIncomeInclVat.Should().Be(0m);
        may.RefundedGross.Should().Be(315m);
        may.StripeFees.Should().Be(6m);
        may.ApplicationFees.Should().Be(2.50m);
    }

    [HumansFact]
    public async Task GetSalesAggregatesAsync_MonthlySales_BucketsByMadridMonthNotUtc()
    {
        // 23:30 UTC on 31 Dec is 00:30 on 1 Jan in Europe/Madrid (UTC+1) — January's books.
        TicketsDb.TicketOrders.Add(
            MakeOrder("ord_ny", TicketPaymentStatus.Paid, Instant.FromUtc(2025, 12, 31, 23, 30), 315m, 0m, 28.64m, 1, 0m));
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetSalesAggregatesAsync();

        result.MonthlySales.Select(m => m.MonthLabel).Should().Equal("2026-01");
    }

    [HumansFact]
    public async Task GetSalesAggregatesAsync_MonthlySales_TicketIncomeExcludesVoidSeatsLikeTheVatBase()
    {
        var orderId = Guid.NewGuid();
        TicketsDb.TicketOrders.Add(new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_void_seat",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 200m,
            DonationAmount = 0m,
            VatAmount = 9.09m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            Attendees =
            [
                MakePricedAttendee(orderId, "tkt_live", "Full Week", 100m, TicketAttendeeStatus.Valid),
                MakePricedAttendee(orderId, "tkt_void", "Full Week", 100m, TicketAttendeeStatus.Void)
            ]
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var month = (await _service.GetSalesAggregatesAsync()).MonthlySales.Single();

        month.GrossRevenue.Should().Be(200m);
        month.TicketsSold.Should().Be(1);
        // The VAT was charged on the one live seat; ticket income must be that same base.
        month.TicketIncomeInclVat.Should().Be(100m);
        month.TicketIncomeExVat.Should().Be(100m - 9.09m);
    }

    [HumansFact]
    public async Task GetSalesAggregatesAsync_MonthlySales_TicketIncomeCountsATransferredSeatOnce()
    {
        // An automated transfer voids the original seat and adds a Valid replacement at the
        // same price on the same order; TotalAmount never moves. The month must report one seat.
        var orderId = Guid.NewGuid();
        TicketsDb.TicketOrders.Add(new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_transferred",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 315m,
            DonationAmount = 0m,
            VatAmount = 28.64m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            Attendees =
            [
                MakePricedAttendee(orderId, "tkt_original", "Full Week", 315m, TicketAttendeeStatus.Void),
                MakePricedAttendee(orderId, "tkt_reissued", "Full Week", 315m, TicketAttendeeStatus.Valid)
            ]
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var month = (await _service.GetSalesAggregatesAsync()).MonthlySales.Single();

        month.TicketsSold.Should().Be(1);
        month.TicketIncomeInclVat.Should().Be(315m);
        month.TicketIncomeExVat.Should().Be(315m - 28.64m);
        month.TicketIncomeExVat.Should().BeGreaterThanOrEqualTo(0m);
    }

    [HumansFact]
    public async Task GetSalesAggregatesAsync_GroupsByTicketTypeAndPrice()
    {
        var orderId = Guid.NewGuid();
        TicketsDb.TicketOrders.Add(new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_by_type",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 700m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            Attendees =
            [
                MakePricedAttendee(orderId, "tkt_fw_1", "Full Week", 100m, TicketAttendeeStatus.Valid),
                MakePricedAttendee(orderId, "tkt_fw_2", "Full Week", 100m, TicketAttendeeStatus.CheckedIn),
                MakePricedAttendee(orderId, "tkt_fw_early", "Full Week", 80m, TicketAttendeeStatus.Valid),
                MakePricedAttendee(orderId, "tkt_vip", "VIP", 420m, TicketAttendeeStatus.Valid)
            ]
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetSalesAggregatesAsync();

        result.ByTicketType.Should().HaveCount(3);

        // Ordered by face value descending.
        var vip = result.ByTicketType[0];
        vip.TicketTypeName.Should().Be("VIP");
        vip.Price.Should().Be(420m);
        vip.TicketsSold.Should().Be(1);
        vip.FaceValue.Should().Be(420m);

        var fullWeek = result.ByTicketType[1];
        fullWeek.TicketTypeName.Should().Be("Full Week");
        fullWeek.Price.Should().Be(100m);
        fullWeek.TicketsSold.Should().Be(2);
        fullWeek.FaceValue.Should().Be(200m);

        var earlyBird = result.ByTicketType[2];
        earlyBird.TicketTypeName.Should().Be("Full Week");
        earlyBird.Price.Should().Be(80m);
        earlyBird.TicketsSold.Should().Be(1);
        earlyBird.FaceValue.Should().Be(80m);
    }

    [HumansFact]
    public async Task GetSalesAggregatesAsync_ByTicketType_ExcludesUnpaidOrdersAndVoidAttendees()
    {
        var paidOrderId = Guid.NewGuid();
        TicketsDb.TicketOrders.Add(new TicketOrder
        {
            Id = paidOrderId,
            VendorOrderId = "ord_paid",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 200m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            Attendees =
            [
                MakePricedAttendee(paidOrderId, "tkt_valid", "Full Week", 100m, TicketAttendeeStatus.Valid),
                MakePricedAttendee(paidOrderId, "tkt_void", "Full Week", 100m, TicketAttendeeStatus.Void)
            ]
        });

        var refundedOrderId = Guid.NewGuid();
        TicketsDb.TicketOrders.Add(new TicketOrder
        {
            Id = refundedOrderId,
            VendorOrderId = "ord_refunded",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 400m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Refunded,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 2, 12, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 2, 12, 0),
            Attendees =
            [
                MakePricedAttendee(refundedOrderId, "tkt_refunded", "VIP", 400m, TicketAttendeeStatus.Valid)
            ]
        });

        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetSalesAggregatesAsync();

        var row = result.ByTicketType.Should().ContainSingle().Subject;
        row.TicketTypeName.Should().Be("Full Week");
        row.Price.Should().Be(100m);
        row.TicketsSold.Should().Be(1);
        row.FaceValue.Should().Be(100m);
    }

    [HumansFact]
    public async Task GetSalesAggregatesAsync_ByTicketType_IsEmptyWithoutData()
    {
        var result = await _service.GetSalesAggregatesAsync();

        result.ByTicketType.Should().BeEmpty();
        result.ByDiscountCampaign.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetSalesAggregatesAsync_ByDiscountCampaign_SplitsGrantedCodesFromVendorCodes()
    {
        var campaignId = Guid.NewGuid();
        StubCodeTracking(
            new CampaignCodeTrackingSummary(campaignId, "Volunteer Comps", TotalGrants: 3, Redeemed: 2),
            [("VOL1", campaignId), ("VOL2", campaignId), ("VOL3", campaignId)]);

        await TicketsDb.TicketOrders.AddRangeAsync(
            MakeDiscountOrder("ord_vol1", TicketPaymentStatus.Paid, "VOL1", 50m),
            MakeDiscountOrder("ord_vol2", TicketPaymentStatus.Paid, "VOL2", 30m),
            MakeDiscountOrder("ord_promo", TicketPaymentStatus.Paid, "PROMO10", 10m),
            MakeDiscountOrder("ord_refunded", TicketPaymentStatus.Refunded, "VOL3", 999m));
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetSalesAggregatesAsync();

        result.ByDiscountCampaign.Should().HaveCount(2);

        var campaign = result.ByDiscountCampaign[0];
        campaign.CampaignTitle.Should().Be("Volunteer Comps");
        campaign.CodesGranted.Should().Be(3);
        campaign.CodesUsed.Should().Be(2);
        campaign.AverageDiscount.Should().Be(40m);
        campaign.TotalDiscount.Should().Be(80m);

        // Vendor codes always sort last and have nothing granted.
        var vendor = result.ByDiscountCampaign[1];
        vendor.CampaignTitle.Should().Be("No campaign (vendor codes)");
        vendor.CodesGranted.Should().BeNull();
        vendor.CodesUsed.Should().Be(1);
        vendor.AverageDiscount.Should().Be(10m);
        vendor.TotalDiscount.Should().Be(10m);
    }

    [HumansFact]
    public async Task GetSalesAggregatesAsync_ByDiscountCampaign_KeepsCampaignsWithNoPaidRedemptions()
    {
        var campaignId = Guid.NewGuid();
        StubCodeTracking(
            new CampaignCodeTrackingSummary(campaignId, "Unused Comps", TotalGrants: 4, Redeemed: 0),
            [("UNUSED1", campaignId), ("UNUSED2", campaignId)]);

        await TicketsDb.TicketOrders.AddAsync(
            MakeDiscountOrder("ord_unpaid", TicketPaymentStatus.Refunded, "UNUSED1", 25m));
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetSalesAggregatesAsync();

        var row = result.ByDiscountCampaign.Should().ContainSingle().Subject;
        row.CampaignTitle.Should().Be("Unused Comps");
        row.CodesGranted.Should().Be(4);
        row.CodesUsed.Should().Be(0);
        row.AverageDiscount.Should().Be(0m);
        row.TotalDiscount.Should().Be(0m);
    }

    private void StubCodeTracking(
        CampaignCodeTrackingSummary summary,
        IEnumerable<(string Code, Guid CampaignId)> grants)
    {
        var grantRows = grants
            .Select(g => new CampaignCodeTrackingGrant(
                Guid.NewGuid(), g.CampaignId, summary.CampaignTitle, Guid.NewGuid(),
                "Recipient", g.Code, RedeemedAt: null, LatestEmailStatus: null))
            .ToList();

        _campaignService.GetCodeTrackingAsync(Arg.Any<CancellationToken>())
            .Returns(new CampaignCodeTrackingData([summary], grantRows));
    }

    private static TicketOrder MakeDiscountOrder(
        string vendorOrderId,
        TicketPaymentStatus paymentStatus,
        string discountCode,
        decimal discountAmount) =>
        new()
        {
            Id = Guid.NewGuid(),
            VendorOrderId = vendorOrderId,
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 100m,
            Currency = "EUR",
            PaymentStatus = paymentStatus,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
            DiscountCode = discountCode,
            DiscountAmount = discountAmount,
        };

    [HumansFact]
    public async Task GetAvailableTicketTypesAsync_ReturnsDistinctTypes()
    {
        var orderId = Guid.NewGuid();
        TicketsDb.TicketOrders.Add(new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_ticket_type_options",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 300m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            Attendees =
            [
                MakeAttendee(orderId, "tkt_weekend", "Weekend"),
                MakeAttendee(orderId, "tkt_vip", "VIP"),
                MakeAttendee(orderId, "tkt_full_week", "Full Week"),
                MakeAttendee(orderId, "tkt_weekend_duplicate", "Weekend")
            ]
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var types = await _service.GetAvailableTicketTypesAsync();

        types.Should().BeEquivalentTo("Full Week", "VIP", "Weekend");
    }

    // ====================================================================
    // GetOrdersPageAsync tests
    // ====================================================================

    [HumansFact]
    public async Task GetOrdersPageAsync_ReturnsPagedResults()
    {
        for (var i = 0; i < 5; i++)
        {
            TicketsDb.TicketOrders.Add(MakeOrder(
                $"ord_{i}", TicketPaymentStatus.Paid,
                Instant.FromUtc(2026, 3, 1 + i, 10, 0),
                100m, 0m, 9.09m, 1, 0m));
        }

        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetOrdersPageAsync(
            null, "date", true, 1, 2, null, null, null);

        result.TotalCount.Should().Be(5);
        result.Rows.Should().HaveCount(2);
    }

    [HumansFact(Timeout = 10000)]
    public async Task GetOrdersPageAsync_FiltersbyPaymentStatus()
    {
        TicketsDb.TicketOrders.Add(MakeOrder("ord_paid", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 1, 10, 0), 100m, 0m, 9.09m, 1, 0m));
        TicketsDb.TicketOrders.Add(MakeOrder("ord_refund", TicketPaymentStatus.Refunded,
            Instant.FromUtc(2026, 3, 2, 10, 0), 200m, 0m, 0m, 1, 0m));
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetOrdersPageAsync(
            null, "date", true, 1, 25, "Paid", null, null);

        result.TotalCount.Should().Be(1);
        result.Rows.Single().VendorOrderId.Should().Be("ord_paid");
    }

    [HumansFact]
    public async Task GetOrdersPageAsync_SortsByAmount()
    {
        TicketsDb.TicketOrders.Add(MakeOrder("ord_cheap", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 1, 10, 0), 50m, 0m, 0m, 1, 0m));
        TicketsDb.TicketOrders.Add(MakeOrder("ord_expensive", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 2, 10, 0), 500m, 0m, 0m, 1, 0m));
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetOrdersPageAsync(
            null, "amount", false, 1, 25, null, null, null);

        result.Rows.First().VendorOrderId.Should().Be("ord_cheap");
        result.Rows.Last().VendorOrderId.Should().Be("ord_expensive");
    }

    // ====================================================================
    // GetAttendeesPageAsync tests
    // ====================================================================

    [HumansFact]
    public async Task GetAttendeesPageAsync_ReturnsPagedResults()
    {
        var order = MakeOrder("ord_1", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 1, 10, 0), 300m, 0m, 0m, 3, 0m);
        TicketsDb.TicketOrders.Add(order);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetAttendeesPageAsync(
            null, "name", false, 1, 2, null, null, null, null);

        result.TotalCount.Should().Be(3);
        result.Rows.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetAttendeesPageAsync_FiltersByTicketType()
    {
        var orderId = Guid.NewGuid();
        var order = new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_types",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 300m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            Attendees =
            [
                new TicketAttendee
                {
                    Id = Guid.NewGuid(), VendorTicketId = "tkt_fw",
                    TicketOrderId = orderId, TicketOrder = null!,
                    AttendeeName = "A1", TicketTypeName = "Full Week",
                    Price = 100m, Status = TicketAttendeeStatus.Valid,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0)
                },
                new TicketAttendee
                {
                    Id = Guid.NewGuid(), VendorTicketId = "tkt_vip",
                    TicketOrderId = orderId, TicketOrder = null!,
                    AttendeeName = "A2", TicketTypeName = "VIP",
                    Price = 400m, Status = TicketAttendeeStatus.Valid,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0)
                }
            ]
        };
        TicketsDb.TicketOrders.Add(order);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetAttendeesPageAsync(
            null, "name", false, 1, 25, "VIP", null, null, null);

        result.TotalCount.Should().Be(1);
        result.Rows.Single().TicketTypeName.Should().Be("VIP");
    }

    [HumansFact]
    public async Task GetAttendeesPageAsync_FiltersByOrderId()
    {
        TicketsDb.TicketOrders.Add(MakeOrder("ord_A", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 1, 10, 0), 100m, 0m, 0m, 1, 0m));
        TicketsDb.TicketOrders.Add(MakeOrder("ord_B", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 2, 10, 0), 200m, 0m, 0m, 2, 0m));
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.GetAttendeesPageAsync(
            null, "name", false, 1, 25, null, null, null, "ord_A");

        result.TotalCount.Should().Be(1);
    }

    // ====================================================================
    // GetWhoHasntBoughtAsync tests
    // ====================================================================

    [HumansFact]
    public async Task GetWhoHasntBoughtAsync_ReturnsActiveHumansWithTicketStatus()
    {
        var userWithTicket = CreateUser("Has Ticket", "hasticket@example.com");
        var userWithout = CreateUser("No Ticket", "noticket@example.com");

        var orderId = Guid.NewGuid();
        TicketsDb.TicketOrders.Add(new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_1",
            BuyerName = "Has Ticket",
            BuyerEmail = "hasticket@example.com",
            TotalAmount = 100m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            MatchedUserId = userWithTicket.Id,
        });

        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        // Wire service dependencies — both users are Volunteers members with Profiles.
        WireWhoHasntBoughtDependencies(userWithTicket, userWithout);

        var result = await _service.GetWhoHasntBoughtAsync(null, null, null, null, 1, 25);

        result.TotalCount.Should().Be(2);
        result.Humans.Should().Contain(h => h.UserId == userWithTicket.Id && h.HasTicket);
        result.Humans.Should().Contain(h => h.UserId == userWithout.Id && !h.HasTicket);
    }

    [HumansFact]
    public async Task GetWhoHasntBoughtAsync_FiltersByTicketStatus()
    {
        var userWithTicket = CreateUser("Has Ticket", "has@example.com");
        var userWithout = CreateUser("No Ticket", "no@example.com");

        TicketsDb.TicketOrders.Add(new TicketOrder
        {
            Id = Guid.NewGuid(),
            VendorOrderId = "ord_1",
            BuyerName = "Has Ticket",
            BuyerEmail = "has@example.com",
            TotalAmount = 100m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            MatchedUserId = userWithTicket.Id,
        });

        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        WireWhoHasntBoughtDependencies(userWithTicket, userWithout);

        var notBought = await _service.GetWhoHasntBoughtAsync(null, null, null, "not_bought", 1, 25);
        notBought.TotalCount.Should().Be(1);
        notBought.Humans.Single().UserId.Should().Be(userWithout.Id);

        var bought = await _service.GetWhoHasntBoughtAsync(null, null, null, "bought", 1, 25);
        bought.TotalCount.Should().Be(1);
        bought.Humans.Single().UserId.Should().Be(userWithTicket.Id);
    }

    [HumansFact]
    public async Task GetWhoHasntBoughtAsync_MatchesBySecondaryVerifiedEmail()
    {
        var target = CreateUser("Target", "primary@example.com");
        target.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = target.Id,
            Email = "secondary@alt.example",
            IsVerified = true,
            IsPrimary = false,
        });
        var other = CreateUser("Other", "other@example.com");

        WireWhoHasntBoughtDependencies(target, other);

        var result = await _service.GetWhoHasntBoughtAsync("alt.example", null, null, null, 1, 25);

        result.TotalCount.Should().Be(1);
        result.Humans.Single().UserId.Should().Be(target.Id);
    }

    [HumansFact]
    public async Task GetWhoHasntBoughtAsync_IgnoresUnverifiedEmailWhenMatching()
    {
        var target = CreateUser("Target", "primary@example.com");
        target.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = target.Id,
            Email = "unverified@alt.example",
            IsVerified = false,
            IsPrimary = false,
        });

        WireWhoHasntBoughtDependencies(target);

        var result = await _service.GetWhoHasntBoughtAsync("alt.example", null, null, null, 1, 25);

        result.TotalCount.Should().Be(0);
    }

    // ====================================================================
    // Export tests
    // ====================================================================

    [HumansFact]
    public async Task GetAttendeeExportDataAsync_ReturnsAllAttendeesOrderedByName()
    {
        var orderId = Guid.NewGuid();
        var order = new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_export",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 200m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            Attendees =
            [
                new TicketAttendee
                {
                    Id = Guid.NewGuid(), VendorTicketId = "tkt_z",
                    TicketOrderId = orderId, TicketOrder = null!,
                    AttendeeName = "Zara", TicketTypeName = "Full Week",
                    Price = 100m, Status = TicketAttendeeStatus.Valid,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0)
                },
                new TicketAttendee
                {
                    Id = Guid.NewGuid(), VendorTicketId = "tkt_a",
                    TicketOrderId = orderId, TicketOrder = null!,
                    AttendeeName = "Alice", TicketTypeName = "VIP",
                    Price = 400m, Status = TicketAttendeeStatus.Valid,
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0)
                }
            ]
        };

        TicketsDb.TicketOrders.Add(order);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var rows = await _service.GetAttendeeExportDataAsync();

        rows.Should().HaveCount(2);
        rows[0].AttendeeName.Should().Be("Alice");
        rows[1].AttendeeName.Should().Be("Zara");
        rows[0].VendorOrderId.Should().Be("ord_export");
    }

    [HumansFact]
    public async Task GetOrderExportDataAsync_ReturnsAllOrdersWithDetails()
    {
        TicketsDb.TicketOrders.Add(MakeOrder("ord_old", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 1, 1, 10, 0), 100m, 5m, 9.09m, 1, 0m));
        TicketsDb.TicketOrders.Add(MakeOrder("ord_new", TicketPaymentStatus.Paid,
            Instant.FromUtc(2026, 3, 1, 10, 0), 200m, 10m, 18.18m, 2, 0m));
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var rows = await _service.GetOrderExportDataAsync();

        rows.Should().HaveCount(2);
        // Ordered by purchase date descending
        rows[0].Date.Should().Be("2026-03-01");
        rows[1].Date.Should().Be("2026-01-01");
        rows[0].AttendeeCount.Should().Be(2);
    }

    // ====================================================================
    // GetTicketOrdersAsync tests
    // ====================================================================

    [HumansFact]
    public async Task GetTicketOrdersAsync_VoidAttendeeWithApprovedTransfer_CarriesRecipientAndBarcode()
    {
        var attendeeId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var decidedAt = Instant.FromUtc(2026, 5, 10, 14, 0);

        var order = new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_transfer",
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = 100m,
            Currency = "EUR",
            PaymentStatus = TicketPaymentStatus.Paid,
            VendorEventId = "ev_test",
            PurchasedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
            Attendees =
            [
                new TicketAttendee
                {
                    Id = attendeeId,
                    VendorTicketId = "tkt_void",
                    TicketOrderId = orderId,
                    TicketOrder = null!,
                    AttendeeName = "Original Holder",
                    TicketTypeName = "Full Week",
                    Price = 100m,
                    Status = TicketAttendeeStatus.Void,
                    Barcode = "BC-001",
                    VendorEventId = "ev_test",
                    SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
                }
            ]
        };

        TicketsDb.TicketOrders.Add(order);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var transfer = new TicketTransferRequest
        {
            Id = Guid.NewGuid(),
            OriginalTicketAttendeeId = attendeeId,
            SenderUserId = Guid.NewGuid(),
            ReceiverUserId = Guid.NewGuid(),
            ReceiverLegalName = "Alice Smith",
            ReceiverEmail = "alice@example.com",
            SenderReason = "Can't attend",
            Status = TicketTransferStatus.Approved,
            RequestedAt = Instant.FromUtc(2026, 5, 1, 10, 0),
            DecidedAt = decidedAt,
        };

        _transferRepo.GetByStatusAsync(TicketTransferStatus.Approved, Arg.Any<CancellationToken>())
            .Returns([transfer]);

        var result = await _service.GetTicketOrdersAsync(Xunit.TestContext.Current.CancellationToken);

        var attendeeInfo = result.Should().ContainSingle().Which
            .Attendees.Should().ContainSingle().Subject;

        attendeeInfo.Barcode.Should().Be("BC-001");
        attendeeInfo.TransferredToName.Should().Be("Alice Smith");
        attendeeInfo.TransferredAt.Should().Be(decidedAt);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private void WireWhoHasntBoughtDependencies(params User[] users)
    {
        var allUsers = users.ToList();
        var userIds = users.Select(u => u.Id).ToList();

        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(
                allUsers.Select(u => u.ToUserInfo(
                    profile: UserFixtures.Profile(
                        membershipTier: MembershipTier.Volunteer))).ToList()));

        _teamService.GetTeamAsync(SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(VolunteersTeam(userIds));

        _userEmailService.GetNotificationEmailsByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(users.ToDictionary(u => u.Id, u => u.Email ?? string.Empty));

        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        _shiftManagementService.GetActiveEventSettingsAsync()
            .Returns((EventSettingsInfo?)null);
    }

    private static User CreateUser(string name, string email)
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = name,
            Email = email,
            UserName = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
        };
        user.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsVerified = true,
            IsPrimary = true,
        });
        return user;
    }

    private static TicketOrder MakeOrder(
        string vendorOrderId,
        TicketPaymentStatus paymentStatus,
        Instant purchasedAt,
        decimal totalAmount,
        decimal donationAmount,
        decimal vatAmount,
        int ticketCount,
        decimal vipPremiumPerTicket)
    {
        var orderId = Guid.NewGuid();
        var attendees = Enumerable.Range(1, ticketCount)
            .Select(i => new TicketAttendee
            {
                Id = Guid.NewGuid(),
                VendorTicketId = $"{vendorOrderId}_tkt_{i}",
                TicketOrderId = orderId,
                TicketOrder = null!,
                AttendeeName = $"Attendee {i}",
                TicketTypeName = vipPremiumPerTicket > 0 ? "VIP" : "Full Week",
                Price = TicketConstants.VipThresholdEuros + vipPremiumPerTicket,
                Status = TicketAttendeeStatus.Valid,
                VendorEventId = "ev_test",
                SyncedAt = purchasedAt,
            })
            .ToList();

        return new TicketOrder
        {
            Id = orderId,
            VendorOrderId = vendorOrderId,
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.com",
            TotalAmount = totalAmount,
            DonationAmount = donationAmount,
            VatAmount = vatAmount,
            Currency = "EUR",
            PaymentStatus = paymentStatus,
            VendorEventId = "ev_test",
            PurchasedAt = purchasedAt,
            SyncedAt = purchasedAt,
            Attendees = attendees,
        };
    }

    private static TicketAttendee MakeAttendee(Guid orderId, string vendorTicketId, string ticketTypeName) =>
        new()
        {
            Id = Guid.NewGuid(),
            VendorTicketId = vendorTicketId,
            TicketOrderId = orderId,
            TicketOrder = null!,
            AttendeeName = ticketTypeName,
            TicketTypeName = ticketTypeName,
            Price = 100m,
            Status = TicketAttendeeStatus.Valid,
            VendorEventId = "ev_test",
            SyncedAt = Instant.FromUtc(2026, 3, 1, 10, 0),
        };

    private static TicketAttendee MakePricedAttendee(
        Guid orderId, string vendorTicketId, string ticketTypeName,
        decimal price, TicketAttendeeStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            VendorTicketId = vendorTicketId,
            TicketOrderId = orderId,
            TicketOrder = null!,
            AttendeeName = vendorTicketId,
            TicketTypeName = ticketTypeName,
            Price = price,
            Status = status,
            VendorEventId = "ev_test",
            SyncedAt = Instant.FromUtc(2026, 3, 2, 10, 0),
        };

    private static TeamInfo VolunteersTeam(IEnumerable<Guid> userIds) =>
        new(
            SystemTeamIds.Volunteers,
            "Volunteers",
            null,
            "volunteers",
            IsActive: true,
            IsSystemTeam: true,
            SystemTeamType.Volunteers,
            RequiresApproval: false,
            IsPublicPage: false,
            IsHidden: false,
            IsPromotedToDirectory: false,
            Instant.FromUtc(2026, 1, 1, 0, 0),
            userIds.Select(userId => new TeamMemberInfo(
                    Guid.NewGuid(), userId, string.Empty, null, null,
                    TeamMemberRole.Member, Instant.FromUtc(2026, 1, 1, 0, 0)))
                .ToList());
}
