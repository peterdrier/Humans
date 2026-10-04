using System.Net;
using AwesomeAssertions;
using Humans.Base.Diagnostics;
using NodaTime;

namespace Humans.TicketTailor.Tests.Services;

public class TicketTailorServiceTests
{
    [HumansTheory]
    [Xunit.InlineData("GetOrdersAsync")]
    [Xunit.InlineData("GetIssuedTicketsAsync")]
    [Xunit.InlineData("GetCheckInsAsync")]
    public async Task PaginatedRead_TimesEachPageSeparately_NotTheWholeLoop(string operation)
    {
        // nobodies-collective/Humans#946: HttpClient.Timeout applies per request and the timing extension's Error
        // threshold is calibrated for one request — timing the whole paginated loop instead
        // falsely trips Error on a long multi-page sync. A timing scope recorded once per
        // page (not once for the whole call) is the fix; verify it via the shared registry,
        // since forcing the real 30s+ elapsed time this threshold judges isn't practical here.
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[] { new { id = "ord_1", issued_ticket_id = "ticket_1", quantity = 1, total = 100, status = "completed", created_at = 1716811200L } },
            links = new { next = "more" }
        });
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[] { new { id = "ord_2", issued_ticket_id = "ticket_2", quantity = 1, total = 100, status = "completed", created_at = 1716811200L } },
            links = new { next = (string?)null }
        });

        var registry = OperationTimingRegistry.Instance;
        var key = $"TicketTailorService.{operation}";
        var before = registry.GetTimings().FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal))?.Count ?? 0;

        var service = TicketTailorTestHost.CreateService(handler);
        switch (operation)
        {
            case "GetOrdersAsync":
                await service.GetOrdersAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);
                break;
            case "GetIssuedTicketsAsync":
                await service.GetIssuedTicketsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);
                break;
            case "GetCheckInsAsync":
                await service.GetCheckInsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);
                break;
        }

        var after = registry.GetTimings().Single(t => string.Equals(t.Key, key, StringComparison.Ordinal)).Count;
        (after - before).Should().Be(2, "one timing scope should be recorded per page, not once for the whole call");
    }

    [HumansFact]
    public async Task GetOrdersAsync_ParsesOrderResponse()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new
                {
                    id = "ord_001",
                    buyer_details = new { first_name = "Jane", last_name = "Doe", email = "jane@example.com", name = "Jane Doe" },
                    total = 15000,
                    currency = new { code = "eur", base_multiplier = 100 },
                    status = "completed",
                    created_at = 1716811200L,
                    line_items = new[]
                    {
                        new { description = "Wave 1 Tickets", type = "ticket", total = 15000 },
                        new { description = "NCA Discount (NOBO25)", type = "gift_card", total = -2500 },
                    }
                }
            },
            links = new { next = (string?)null }
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var orders = await service.GetOrdersAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        orders.Should().HaveCount(1);
        orders[0].BuyerName.Should().Be("Jane Doe");
        orders[0].TotalAmount.Should().Be(150m);
        orders[0].DiscountCode.Should().Be("NOBO25");
    }

    [HumansFact]
    public async Task GetOrdersAsync_HandlesPagination()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new
                {
                    id = "ord_001",
                    buyer_details = new { first_name = "A", last_name = "B", email = "a@b.com", name = "A B" },
                    total = 100, currency = new { code = "eur", base_multiplier = 100 },
                    voucher_code = (string?)null, status = "completed", created_at = 1716811200L
                }
            },
            links = new { next = "has_more" }
        });
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new
                {
                    id = "ord_002",
                    buyer_details = new { first_name = "C", last_name = "D", email = "c@d.com", name = "C D" },
                    total = 200, currency = new { code = "eur", base_multiplier = 100 },
                    voucher_code = (string?)null, status = "completed", created_at = 1716811200L
                }
            },
            links = new { next = (string?)null }
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var orders = await service.GetOrdersAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        orders.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetOrdersAsync_ThrowsOnApiError()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.Unauthorized, new { error = "Invalid API key" });

        var service = TicketTailorTestHost.CreateService(handler);
        var act = () => service.GetOrdersAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [HumansFact]
    public async Task GetIssuedTicketsAsync_ParsesTicketResponse()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new
                {
                    id = "it_001",
                    first_name = "Jane",
                    last_name = "Doe",
                    full_name = "Jane Doe",
                    email = "jane@example.com",
                    description = "Full Week",
                    listed_price = 15000,
                    status = "valid",
                    order_id = "ord_001"
                }
            },
            links = new { next = (string?)null }
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var tickets = await service.GetIssuedTicketsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        tickets.Should().HaveCount(1);
        tickets[0].AttendeeName.Should().Be("Jane Doe");
        tickets[0].AttendeeEmail.Should().Be("jane@example.com");
        tickets[0].Price.Should().Be(150m);
        tickets[0].TicketTypeName.Should().Be("Full Week");
        tickets[0].VendorOrderId.Should().Be("ord_001");
    }

    [HumansFact]
    public async Task GetIssuedTicketsAsync_PrefersCustomQuestionEmailOverTopLevelEmail()
    {
        // Real-world TT shape (order or_75997215, ticket it_124025964): the
        // top-level `email` is the buyer's account email replicated onto each
        // ticket; the actual attendee email lives in custom_questions where
        // question == "Email".
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new
                {
                    id = "it_124025964",
                    first_name = "Daniel",
                    last_name = "Paiva De Miranda",
                    full_name = "Daniel Paiva De Miranda",
                    email = "yulia.kisd@gmail.com",
                    custom_questions = new[]
                    {
                        new { question = "Email", answer = "dpmirandadp@gmail.com" }
                    },
                    description = "Main tickets - Wave 2 Tickets",
                    listed_price = 29500,
                    status = "valid",
                    order_id = "or_75997215"
                }
            },
            links = new { next = (string?)null }
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var tickets = await service.GetIssuedTicketsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        tickets[0].AttendeeEmail.Should().Be("dpmirandadp@gmail.com");
    }

    [HumansFact]
    public async Task GetIssuedTicketsAsync_FallsBackToTopLevelEmailWhenCustomAnswerBlank()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new
                {
                    id = "it_002",
                    first_name = "Jane",
                    last_name = "Doe",
                    full_name = "Jane Doe",
                    email = "jane@example.com",
                    custom_questions = new[]
                    {
                        new { question = "Email", answer = "   " }
                    },
                    description = "Full Week",
                    listed_price = 15000,
                    status = "valid",
                    order_id = "ord_001"
                }
            },
            links = new { next = (string?)null }
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var tickets = await service.GetIssuedTicketsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        tickets[0].AttendeeEmail.Should().Be("jane@example.com");
    }

    [HumansFact]
    public async Task GetIssuedTicketsAsync_RequiresExactEmailQuestionString()
    {
        // Match is case-sensitive and exact: "email" / "Email Address" / "Your Email"
        // must not match, and unrelated questions are ignored. Only a question
        // whose text is exactly "Email" qualifies.
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new
                {
                    id = "it_004",
                    first_name = "Jane",
                    last_name = "Doe",
                    full_name = "Jane Doe",
                    email = "jane@example.com",
                    custom_questions = new[]
                    {
                        new { question = "Dietary restrictions", answer = "vegan" },
                        new { question = "email", answer = "lower@example.com" },
                        new { question = "Email Address", answer = "labelled@example.com" },
                        new { question = "Your Email", answer = "phrased@example.com" }
                    },
                    description = "Full Week",
                    listed_price = 15000,
                    status = "valid",
                    order_id = "ord_001"
                }
            },
            links = new { next = (string?)null }
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var tickets = await service.GetIssuedTicketsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        tickets[0].AttendeeEmail.Should().Be("jane@example.com");
    }

    [HumansFact]
    public async Task GetEventSummaryAsync_ParsesEventResponse()
    {
        var handler = new RecordingHttpHandler();
        var responseContent = handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            name = "Elsewhere 2026",
            total_holds = 0,
            total_issued_tickets = 96,
            total_orders = 88,
            ticket_types = new[]
            {
                new { quantity_total = 2000, quantity_issued = 86 },
                new { quantity_total = 500, quantity_issued = 6 },
                new { quantity_total = 500, quantity_issued = 4 },
            },
            ticket_groups = new[]
            {
                new { name = "Main tickets", max_quantity = 2000 },
            }
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var summary = await service.GetEventSummaryAsync("ev_test", Xunit.TestContext.Current.CancellationToken);

        summary.EventName.Should().Be("Elsewhere 2026");
        responseContent.WasDisposed.Should().BeTrue();
        summary.TotalCapacity.Should().Be(2000);
        summary.TicketsSold.Should().Be(96);
        summary.TicketsRemaining.Should().Be(1904);
    }

    [HumansFact]
    public async Task GetEventSummaryAsync_RejectsMissingEventInsteadOfInventingAnEmptySummary()
    {
        var handler = new RecordingHttpHandler();
        var content = handler.EnqueueResponse(HttpStatusCode.OK, null);
        var service = TicketTailorTestHost.CreateService(handler);

        var load = () => service.GetEventSummaryAsync("ev_test", Xunit.TestContext.Current.CancellationToken);

        await load.Should().ThrowAsync<HttpRequestException>();
        content.WasDisposed.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetEventSummaryAsync_FallsBackToTicketTypeTotalsWhenNoGroups()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            name = "Elsewhere 2026",
            total_issued_tickets = 10,
            ticket_types = new[] { new { quantity_total = 300 }, new { quantity_total = 200 } },
            ticket_groups = Array.Empty<object>()
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var summary = await service.GetEventSummaryAsync("ev_test", Xunit.TestContext.Current.CancellationToken);

        summary.TotalCapacity.Should().Be(500);
        summary.TicketsRemaining.Should().Be(490);
    }

    [HumansFact]
    public async Task GetCheckInsAsync_NetsQuantity_OmitsCheckedOutTickets()
    {
        // TicketTailor /check_ins returns checkout/undo records (quantity = -1)
        // alongside check-ins (quantity = +1). A ticket whose records net to zero
        // (checked in then out), or a bare checkout, must not be reported onsite.
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new { id = "ci_1", issued_ticket_id = "it_in", check_in_at = 1751983320L, created_at = 1751983320L, quantity = 1 },
                new { id = "ci_2", issued_ticket_id = "it_out", check_in_at = 1751983300L, created_at = 1751983300L, quantity = 1 },
                new { id = "ci_3", issued_ticket_id = "it_out", check_in_at = 1751990000L, created_at = 1751990000L, quantity = -1 },
                new { id = "ci_4", issued_ticket_id = "it_undo", check_in_at = 1751990500L, created_at = 1751990500L, quantity = -1 },
            },
            links = new { next = (string?)null }
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var checkIns = await service.GetCheckInsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        checkIns.Should().ContainSingle(c => c.VendorTicketId == "it_in");
        checkIns.Should().NotContain(c => c.VendorTicketId == "it_out");
        checkIns.Should().NotContain(c => c.VendorTicketId == "it_undo");
        checkIns.Single().CheckedInAt.Should().Be(Instant.FromUnixTimeSeconds(1751983320L));
    }

    [HumansFact]
    public async Task GetCheckInsAsync_ReportsEarliestPositiveScan_FallingBackToCreatedAt()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new object[]
            {
                new { id = "ci_1", issued_ticket_id = "it_twice", check_in_at = 1751990000L, created_at = 1751990000L, quantity = 1 },
                new { id = "ci_2", issued_ticket_id = "it_twice", check_in_at = 1751983320L, created_at = 1751995000L, quantity = 1 },
                new { id = "ci_3", issued_ticket_id = "it_offline", created_at = 1751984000L, quantity = 1 },
            },
            links = new { next = (string?)null }
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var checkIns = await service.GetCheckInsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        checkIns.Single(c => string.Equals(c.VendorTicketId, "it_twice", StringComparison.Ordinal)).CheckedInAt.Should().Be(Instant.FromUnixTimeSeconds(1751983320L));
        checkIns.Single(c => string.Equals(c.VendorTicketId, "it_offline", StringComparison.Ordinal)).CheckedInAt.Should().Be(Instant.FromUnixTimeSeconds(1751984000L));
    }

    [HumansFact]
    public async Task Since_FiltersOrdersAndTicketsByUpdatedAtAndCheckInsByCreatedAt()
    {
        var handler = new RecordingHttpHandler();
        for (var i = 0; i < 3; i++)
            handler.EnqueueResponse(HttpStatusCode.OK, new { data = Array.Empty<object>(), links = new { next = (string?)null } });

        var service = TicketTailorTestHost.CreateService(handler);
        var since = Instant.FromUnixTimeSeconds(1700000000L);
        await service.GetOrdersAsync(since, "ev_test", Xunit.TestContext.Current.CancellationToken);
        await service.GetIssuedTicketsAsync(since, "ev_test", Xunit.TestContext.Current.CancellationToken);
        await service.GetCheckInsAsync(since, "ev_test", Xunit.TestContext.Current.CancellationToken);

        var urls = handler.Requests.Select(r => r.Request.RequestUri!.ToString()).ToList();
        urls[0].Should().Contain("/orders?event_id=ev_test").And.Contain("updated_at.gte=1700000000");
        urls[1].Should().Contain("/issued_tickets?event_id=ev_test").And.Contain("updated_at.gte=1700000000");
        urls[2].Should().Contain("/check_ins?event_id=ev_test").And.Contain("created_at.gte=1700000000");
    }

    [HumansTheory]
    [Xunit.InlineData("orders", "repeat")]
    [Xunit.InlineData("tickets", "repeat")]
    [Xunit.InlineData("check-ins", "repeat")]
    [Xunit.InlineData("orders", "cycle")]
    [Xunit.InlineData("tickets", "cycle")]
    [Xunit.InlineData("check-ins", "cycle")]
    [Xunit.InlineData("orders", "missing-cursor")]
    [Xunit.InlineData("tickets", "missing-cursor")]
    [Xunit.InlineData("check-ins", "missing-cursor")]
    [Xunit.InlineData("orders", "empty-continuation")]
    [Xunit.InlineData("tickets", "empty-continuation")]
    [Xunit.InlineData("check-ins", "empty-continuation")]
    [Xunit.InlineData("orders", "missing-data")]
    [Xunit.InlineData("tickets", "missing-data")]
    [Xunit.InlineData("check-ins", "missing-data")]
    [Xunit.InlineData("orders", "null-item")]
    [Xunit.InlineData("tickets", "null-item")]
    [Xunit.InlineData("check-ins", "null-item")]
    [Xunit.InlineData("orders", "missing-identity")]
    [Xunit.InlineData("tickets", "missing-identity")]
    [Xunit.InlineData("check-ins", "missing-identity")]
    [Xunit.InlineData("orders", "blank-identity")]
    [Xunit.InlineData("tickets", "blank-identity")]
    [Xunit.InlineData("check-ins", "blank-identity")]
    [Xunit.InlineData("orders", "whitespace-identity")]
    [Xunit.InlineData("tickets", "whitespace-identity")]
    [Xunit.InlineData("check-ins", "whitespace-identity")]
    public async Task Paging_RejectsInvalidContinuationWithoutReturningPartialData(string endpoint, string shape)
    {
        var handler = new RecordingHttpHandler();
        var expectedRequests = 1;
        if (shape is "null-item" or "missing-identity" or "blank-identity" or "whitespace-identity")
        {
            object? invalidItem = shape switch
            {
                "null-item" => null,
                "missing-identity" => new { created_at = 1716811200L },
                "blank-identity" => new { id = "", created_at = 1716811200L },
                _ => new { id = "  ", created_at = 1716811200L },
            };
            // The invalid item is not the continuation row: every item needs an identity.
            handler.EnqueueResponse(HttpStatusCode.OK, new
            {
                data = new[] { invalidItem, new { id = "valid", created_at = 1716811200L } },
                links = new { next = (string?)null },
            });
        }
        else if (string.Equals(shape, "missing-data", StringComparison.Ordinal))
            handler.EnqueueResponse(HttpStatusCode.OK, new { links = new { next = "more" } });
        else if (string.Equals(shape, "empty-continuation", StringComparison.Ordinal))
            handler.EnqueueResponse(HttpStatusCode.OK, new { data = Array.Empty<object>(), links = new { next = "more" } });
        else
        {
            var pageIds = shape switch
            {
                "repeat" => new string?[] { "page-a", "page-a" },
                "cycle" => ["page-a", "page-b", "page-a"],
                _ => [null],
            };
            expectedRequests = pageIds.Length;
            foreach (var id in pageIds)
                handler.EnqueueResponse(HttpStatusCode.OK, new
                {
                    data = new[] { new { id, created_at = 1716811200L } },
                    links = new { next = "more" }
                });
        }
        // A faulty loop could otherwise hang the test; this terminal page lets it return a wrong result.
        handler.EnqueueResponse(HttpStatusCode.OK, new { data = Array.Empty<object>(), links = new { next = (string?)null } });
        var service = TicketTailorTestHost.CreateService(handler);
        Func<Task> act = async () =>
        {
            if (string.Equals(endpoint, "orders", StringComparison.Ordinal))
                await service.GetOrdersAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);
            else if (string.Equals(endpoint, "tickets", StringComparison.Ordinal))
                await service.GetIssuedTicketsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);
            else
                await service.GetCheckInsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);
        };

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*pagination*");
        handler.RequestCount.Should().Be(expectedRequests);
    }

    [HumansFact]
    public async Task Paging_FollowsLinksNextByStartingAfterTheLastId()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[] { new { id = "it_1", order_id = "ord_1" }, new { id = "it_2", order_id = "ord_1" } },
            links = new { next = "more" }
        });
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[] { new { id = "it_3", order_id = "ord_2" } },
            links = new { next = (string?)null }
        });
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[] { new { id = "ci_1", issued_ticket_id = "it_1", created_at = 1751983320L, quantity = 1 } },
            links = new { next = "more" }
        });
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            data = new[] { new { id = "ci_2", issued_ticket_id = "it_2", created_at = 1751983330L, quantity = 1 } },
            links = new { next = (string?)null }
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var tickets = await service.GetIssuedTicketsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);
        var checkIns = await service.GetCheckInsAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        tickets.Should().HaveCount(3);
        checkIns.Should().HaveCount(2);
        var urls = handler.Requests.Select(r => r.Request.RequestUri!.ToString()).ToList();
        urls[0].Should().NotContain("starting_after");
        urls[1].Should().EndWith("starting_after=it_2");
        urls[2].Should().NotContain("starting_after");
        urls[3].Should().EndWith("starting_after=ci_1");
    }

    [HumansFact]
    public async Task ApiKey_IsSentAsBasicAuthOnlyWhenPresent()
    {
        var withKey = new RecordingHttpHandler();
        withKey.EnqueueResponse(HttpStatusCode.OK, new { data = Array.Empty<object>(), links = new { next = (string?)null } });
        var withoutKey = new RecordingHttpHandler();
        withoutKey.EnqueueResponse(HttpStatusCode.OK, new { data = Array.Empty<object>(), links = new { next = (string?)null } });

        await TicketTailorTestHost.CreateService(withKey, apiKey: "sk_test")
            .GetOrdersAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);
        await TicketTailorTestHost.CreateService(withoutKey, apiKey: "")
            .GetOrdersAsync(null, "ev_test", Xunit.TestContext.Current.CancellationToken);

        var auth = withKey.Requests.Single().Request.Headers.Authorization;
        auth!.Scheme.Should().Be("Basic");
        auth.Parameter.Should().Be(Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("sk_test:")));
        withoutKey.Requests.Single().Request.Headers.Authorization.Should().BeNull();
    }
}
