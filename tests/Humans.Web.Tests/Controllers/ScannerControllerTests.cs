using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.ICalFeed;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public class ScannerControllerTests
{
    private static ScannerController NewController(
        ITicketServiceRead tickets,
        IUserServiceRead? users = null,
        IEarlyEntryService? earlyEntry = null,
        IConsentServiceRead? consents = null,
        IICalFeedService? calendarFeed = null,
        IEventServiceRead? events = null,
        IBurnSettingsService? burnSettings = null)
    {
        var ctrl = new ScannerController(
            tickets,
            users ?? Substitute.For<IUserServiceRead>(),
            earlyEntry ?? Substitute.For<IEarlyEntryService>(),
            consents ?? Substitute.For<IConsentServiceRead>(),
            calendarFeed ?? Substitute.For<IICalFeedService>(),
            events ?? Substitute.For<IEventServiceRead>(),
            burnSettings ?? Substitute.For<IBurnSettingsService>());

        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Card" },
        };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return ctrl;
    }

    private static ITicketServiceRead TicketsWithAttendee(TicketAttendeeInfo attendee)
    {
        var order = new TicketOrderInfo(
            Id: Guid.NewGuid(),
            VendorOrderId: "ord-1",
            BuyerName: "Buyer",
            BuyerEmail: "buyer@example.com",
            TotalAmount: 10m,
            Currency: "EUR",
            DiscountCode: null,
            PaymentStatus: TicketPaymentStatus.Paid,
            VendorEventId: "evt-1",
            PurchasedAt: Instant.FromUtc(2026, 6, 1, 12, 0),
            MatchedUserId: null,
            IsCurrentEvent: true,
            Attendees: new[] { attendee });

        var tickets = Substitute.For<ITicketServiceRead>();
        tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { order });
        return tickets;
    }

    private static TicketAttendeeInfo Attendee(
        Guid? matchedUserId = null, TicketAttendeeStatus status = TicketAttendeeStatus.Valid) => new(
        Id: Guid.NewGuid(),
        VendorTicketId: "vt-1",
        AttendeeName: "Ada Lovelace",
        AttendeeEmail: "ada@example.com",
        TicketTypeName: "General Admission",
        Price: 10m,
        Status: status,
        MatchedUserId: matchedUserId,
        Barcode: "xyz34Qy5");

    private static BurnSettingsInfo ActiveBurn(int year = 2026) => new(
        Id: Guid.NewGuid(),
        EventName: "Elsewhere",
        Year: year,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(year, 6, 17),
        BuildStartOffset: -10,
        EventEndOffset: 5,
        StrikeEndOffset: 10,
        FirstCrewStartOffset: -14,
        SetupWeekStartOffset: -7,
        PreEventWeekStartOffset: -3,
        FinishingWeekendStartOffset: 3,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null);

    private static ApprovedEventView OfferedEvent(
        Guid submitterUserId, Guid? campId, string title, Instant startAt,
        bool isRecurring = false, string? recurrenceDays = null) => new(
        Id: Guid.NewGuid(),
        CampId: campId,
        GuideSharedVenueId: null,
        SubmitterUserId: submitterUserId,
        CategoryId: Guid.NewGuid(),
        CategorySlug: "workshops",
        CategoryName: "Workshops",
        CategoryIsSensitive: false,
        VenueName: "The Dome",
        Title: title,
        Description: "desc",
        LocationNote: null,
        Host: "Ada",
        StartAt: startAt,
        DurationMinutes: 90,
        IsRecurring: isRecurring,
        RecurrenceDays: recurrenceDays,
        PriorityRank: 0,
        SubmittedAt: Instant.FromUtc(2026, 5, 1, 0, 0),
        LastUpdatedAt: Instant.FromUtc(2026, 5, 1, 0, 0));

    [HumansFact]
    public async Task Card_MatchingBarcode_ReturnsFoundCard()
    {
        var tickets = TicketsWithAttendee(Attendee());
        var ctrl = NewController(tickets);

        var result = await ctrl.Card("xyz34Qy5", Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ScannerTicketCardViewModel>().Subject;
        vm.Found.Should().BeTrue();
        vm.ScannedBarcode.Should().Be("xyz34Qy5");
        vm.TicketTypeName.Should().Be("General Admission");
        vm.Stub.Should().NotBeNull();
        vm.Stub!.AttendeeName.Should().Be("Ada Lovelace");
        vm.Stub.AttendeeEmail.Should().Be("ada@example.com");
        vm.Stub.Status.Should().Be(TicketAttendeeStatus.Valid);
    }

    [HumansFact]
    public async Task Card_VoidAttendeeWithTransfer_CarriesTransferFields()
    {
        var transferredAt = Instant.FromUtc(2026, 6, 5, 9, 0);
        var attendee = new TicketAttendeeInfo(
            Id: Guid.NewGuid(),
            VendorTicketId: "vt-void",
            AttendeeName: "Old Owner",
            AttendeeEmail: "old@example.com",
            TicketTypeName: "General Admission",
            Price: 10m,
            Status: TicketAttendeeStatus.Void,
            MatchedUserId: null,
            Barcode: "vb1",
            TransferredToName: "Alice Receiver",
            TransferredAt: transferredAt);
        var tickets = TicketsWithAttendee(attendee);
        var ctrl = NewController(tickets);

        var result = await ctrl.Card("vb1", Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ScannerTicketCardViewModel>().Subject;
        vm.Found.Should().BeTrue();
        vm.TransferredToName.Should().Be("Alice Receiver");
        vm.TransferredAt.Should().Be(transferredAt);
    }

    [HumansFact]
    public async Task Card_UnmatchedBarcode_ReturnsNotFoundCard()
    {
        var tickets = TicketsWithAttendee(Attendee());
        var ctrl = NewController(tickets);

        var result = await ctrl.Card("nope-0000", Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ScannerTicketCardViewModel>().Subject;
        vm.Found.Should().BeFalse();
        vm.ScannedBarcode.Should().Be("nope-0000");
        vm.Stub.Should().BeNull();
    }

    [HumansFact]
    public async Task Card_NoMatchedUser_SkipsPerPersonEnrichment()
    {
        var tickets = TicketsWithAttendee(Attendee(matchedUserId: null));
        var earlyEntry = Substitute.For<IEarlyEntryService>();
        var consents = Substitute.For<IConsentServiceRead>();
        var users = Substitute.For<IUserServiceRead>();
        var ctrl = NewController(tickets, users: users, earlyEntry: earlyEntry, consents: consents);

        var result = await ctrl.Card("xyz34Qy5", Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ScannerTicketCardViewModel>().Subject;
        vm.Stub!.EarlyEntryDate.Should().BeNull();
        vm.EarlyEntrySources.Should().BeNull();
        vm.CheckedInAt.Should().BeNull();
        vm.PendingConsents.Should().BeNull();
        vm.ProvideItems.Should().BeNull();
        await earlyEntry.DidNotReceive().GetForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await consents.DidNotReceive().GetPendingDocumentNamesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await users.DidNotReceive().GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Card_MatchedUser_EnrichesCardWithDoorContext()
    {
        var userId = Guid.NewGuid();
        var checkedInAt = Instant.FromUtc(2026, 6, 17, 10, 30);
        var tickets = TicketsWithAttendee(Attendee(userId, TicketAttendeeStatus.CheckedIn));

        var earlyEntry = Substitute.For<IEarlyEntryService>();
        earlyEntry.GetForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserEarlyEntry(new LocalDate(2026, 6, 15), ["Teams: Gate"]));

        var consents = Substitute.For<IConsentServiceRead>();
        consents.GetPendingDocumentNamesAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new[] { "Liability Waiver" });

        var burnSettings = Substitute.For<IBurnSettingsService>();
        burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(ActiveBurn());

        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en" },
            [],
            [
                // Prior year's check-in must not leak into the current card.
                new EventParticipation
                {
                    Id = Guid.NewGuid(), UserId = userId, Year = 2025,
                    Status = ParticipationStatus.Attended, Source = ParticipationSource.TicketSync,
                    CheckedInAt = Instant.FromUtc(2025, 6, 18, 9, 0),
                },
                new EventParticipation
                {
                    Id = Guid.NewGuid(), UserId = userId, Year = 2026,
                    Status = ParticipationStatus.Attended, Source = ParticipationSource.TicketSync,
                    CheckedInAt = checkedInAt,
                },
            ],
            [], profile: null, [], [], [], []));

        var calendarFeed = Substitute.For<IICalFeedService>();
        var shiftItem = new CalendarFeedItem(
            "shift-1@humans.nobodies.team", "Shifts", "Gate: Morning",
            null, Instant.FromUtc(2026, 6, 18, 8, 0), Instant.FromUtc(2026, 6, 18, 12, 0), null, null);
        var favouritedItem = new CalendarFeedItem(
            "event-fav-20260618@humans.nobodies.team", "Events", "Someone Else's Concert",
            null, Instant.FromUtc(2026, 6, 18, 20, 0), Instant.FromUtc(2026, 6, 18, 22, 0), null, null);
        calendarFeed.GetFeedItemsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new[] { shiftItem, favouritedItem });

        var events = Substitute.For<IEventServiceRead>();
        events.GetApprovedEventsAsync(null, null, null, null, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                OfferedEvent(userId, campId: null, "Intro to Soldering", Instant.FromUtc(2026, 6, 17, 16, 0)),
                // Recurring on gate-opening day offsets 0 and 2 (gate opens 2026-06-17):
                // expands to 2026-06-17 and 2026-06-19 at 08:00 Madrid (06:00 UTC).
                OfferedEvent(userId, campId: null, "Daily Yoga", Instant.FromUtc(2026, 6, 18, 6, 0),
                    isRecurring: true, recurrenceDays: "0,2"),
                OfferedEvent(userId, campId: Guid.NewGuid(), "Camp Workshop", Instant.FromUtc(2026, 6, 18, 16, 0)),
                OfferedEvent(Guid.NewGuid(), campId: null, "Other Human's Talk", Instant.FromUtc(2026, 6, 18, 17, 0)),
            });

        var ctrl = NewController(tickets, users, earlyEntry, consents, calendarFeed, events, burnSettings);

        var result = await ctrl.Card("xyz34Qy5", Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ScannerTicketCardViewModel>().Subject;
        vm.Stub!.EarlyEntryDate.Should().Be(new LocalDate(2026, 6, 15));
        vm.EarlyEntrySources.Should().Equal("Teams: Gate");
        vm.CheckedInAt.Should().Be(checkedInAt);
        vm.PendingConsents.Should().Equal("Liability Waiver");
        vm.BurnTimeZone.Should().NotBeNull();
        vm.BurnTimeZone!.Id.Should().Be("Europe/Madrid");

        // Provide list: offered workshop + recurring occurrences + shift commitment,
        // sorted by start; favourited feed events, camp submissions, and other
        // humans' events excluded.
        vm.ProvideItems.Should().NotBeNull();
        vm.ProvideItems!.Select(i => i.Summary).Should().Equal(
            "Daily Yoga", "Intro to Soldering", "Gate: Morning", "Daily Yoga");
        vm.ProvideItems.Select(i => i.Source).Should().Equal("Events", "Events", "Shifts", "Events");
        vm.ProvideItems[0].Start.Should().Be(Instant.FromUtc(2026, 6, 17, 6, 0));
        vm.ProvideItems[3].Start.Should().Be(Instant.FromUtc(2026, 6, 19, 6, 0));
    }

    [HumansFact]
    public async Task Card_MatchedUserAllConsentsSigned_PendingListIsEmptyNotNull()
    {
        var userId = Guid.NewGuid();
        var tickets = TicketsWithAttendee(Attendee(userId));
        var consents = Substitute.For<IConsentServiceRead>();
        consents.GetPendingDocumentNamesAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());

        var ctrl = NewController(tickets, consents: consents);

        var result = await ctrl.Card("xyz34Qy5", Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<PartialViewResult>().Subject
            .Model.Should().BeOfType<ScannerTicketCardViewModel>().Subject;
        vm.PendingConsents.Should().NotBeNull();
        vm.PendingConsents.Should().BeEmpty();
        vm.ProvideItems.Should().NotBeNull();
        vm.ProvideItems.Should().BeEmpty();
        // No active burn configured on the default substitute → no check-in lookup.
        vm.CheckedInAt.Should().BeNull();
    }
}
