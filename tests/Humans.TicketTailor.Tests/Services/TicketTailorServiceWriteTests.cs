using System.Net;
using System.Text.Json.Nodes;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using AwesomeAssertions;
using Humans.Tickets.Contracts;
using NodaTime;
using Xunit;

namespace Humans.TicketTailor.Tests.Services;

public class TicketTailorServiceWriteTests
{
    [HumansFact]
    public async Task VoidIssuedTicketAsync_VoidToHold_True_PostsCorrectFormBody()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "tt_xyz",
            hold_id = "hold_abc",
            voided = "yes"
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var result = await service.VoidIssuedTicketAsync("tt_xyz", voidToHold: true);

        result.VendorTicketId.Should().Be("tt_xyz");
        result.HoldId.Should().Be("hold_abc");
        handler.Requests.Single().Body.Should().Contain("void_to_hold=true");
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_VoidToHold_False_PostsFalseBody()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "tt_xyz",
            hold_id = (string?)null,
            voided = "yes"
        });

        var service = TicketTailorTestHost.CreateService(handler);
        await service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        handler.Requests.Single().Body.Should().Contain("void_to_hold=false");
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_ResponseWithNoHoldId_ReturnsNullHoldId()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "tt_xyz",
            voided = "yes"
            // no hold_id field
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var result = await service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        result.VendorTicketId.Should().Be("tt_xyz");
        result.HoldId.Should().BeNull();
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_PostsToCorrectUrl()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "tt_abc",
            voided = "yes"
        });

        var service = TicketTailorTestHost.CreateService(handler);
        await service.VoidIssuedTicketAsync("tt_abc", voidToHold: true);

        handler.Requests.Single().Request.RequestUri!.ToString().Should().Contain("/v1/issued_tickets/tt_abc/void");
    }

    [HumansTheory]
    [InlineData(HttpStatusCode.BadRequest, TicketVendorFailureKind.Validation)]
    [InlineData(HttpStatusCode.UnprocessableEntity, TicketVendorFailureKind.Validation)]
    [InlineData(HttpStatusCode.Unauthorized, TicketVendorFailureKind.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, TicketVendorFailureKind.AuthFailed)]
    [InlineData(HttpStatusCode.NotFound, TicketVendorFailureKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, TicketVendorFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, TicketVendorFailureKind.Transient)]
    public async Task VoidIssuedTicketAsync_MapsStatusToFailureKind(HttpStatusCode status, TicketVendorFailureKind kind)
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(status, new { error = "vendor says no" });

        var service = TicketTailorTestHost.CreateService(handler);
        var act = () => service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(kind);
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_TransportException_ThrowsTransient()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueThrow(new HttpRequestException("network failure"));

        var service = TicketTailorTestHost.CreateService(handler);
        var act = () => service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.Transient);
        ex.Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [HumansFact]
    public async Task IssueTicketAsync_NeitherHoldNorEventAndType_ThrowsArgumentExceptionBeforeAnyCall()
    {
        var handler = new RecordingHttpHandler();
        var service = TicketTailorTestHost.CreateService(handler);
        var request = new IssueTicketRequest(
            EventId: null,
            TicketTypeId: null,
            HoldId: null,
            FullName: "Jane Doe",
            Email: "jane@example.com",
            SendEmail: false,
            ExternalReference: null);

        var act = () => service.IssueTicketAsync(request);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.RequestCount.Should().Be(0);
    }

    [HumansFact]
    public async Task IssueTicketAsync_WithHoldId_PostsHoldIdFormKey()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "it_new",
            full_name = "Jane Doe",
            email = "jane@example.com",
            description = "Full Week",
            listed_price = 40000,
            status = "valid",
            order_id = (string?)null
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var request = new IssueTicketRequest(
            EventId: null,
            TicketTypeId: null,
            HoldId: "hold_xyz",
            FullName: "Jane Doe",
            Email: "jane@example.com",
            SendEmail: false,
            ExternalReference: null);

        var result = await service.IssueTicketAsync(request);

        var body = handler.Requests.Single().Body;
        body.Should().Contain("hold_id=hold_xyz");
        body.Should().NotContain("event_id");
        body.Should().NotContain("ticket_type_id");
        result.VendorTicketId.Should().Be("it_new");
    }

    [HumansFact]
    public async Task IssueTicketAsync_WithEventAndType_PostsEventAndTypeFormKeys()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "it_new2",
            full_name = "Bob Smith",
            email = "bob@example.com",
            description = "Full Week",
            listed_price = 40000,
            status = "valid",
            order_id = (string?)null
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var request = new IssueTicketRequest(
            EventId: "ev_test",
            TicketTypeId: "tt_type_001",
            HoldId: null,
            FullName: "Bob Smith",
            Email: "bob@example.com",
            SendEmail: false,
            ExternalReference: null);

        await service.IssueTicketAsync(request);

        var body = handler.Requests.Single().Body;
        body.Should().Contain("event_id=ev_test");
        body.Should().Contain("ticket_type_id=tt_type_001");
        body.Should().NotContain("hold_id");
    }

    [HumansFact]
    public async Task IssueTicketAsync_SetsFullNameAndSendEmailInBody()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "it_new3",
            full_name = "Carol White",
            email = "carol@example.com",
            description = "Full Week",
            listed_price = 40000,
            status = "valid",
            order_id = (string?)null
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var request = new IssueTicketRequest(
            EventId: "ev_test",
            TicketTypeId: "tt_type_001",
            HoldId: null,
            FullName: "Carol White",
            Email: "carol@example.com",
            SendEmail: true,
            ExternalReference: "ref_abc");

        await service.IssueTicketAsync(request);

        var body = handler.Requests.Single().Body;
        body.Should().Contain("full_name=Carol+White");
        body.Should().Contain("send_email=true");
        body.Should().Contain("email=carol%40example.com");
        body.Should().Contain("reference=ref_abc");
    }

    [HumansFact]
    public async Task IssueTicketAsync_MapsResponseToVendorTicketDto()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "it_mapped",
            full_name = "Dana Brown",
            email = "dana@example.com",
            description = "Weekend Pass",
            listed_price = 20000,
            status = "valid",
            order_id = (string?)null,
            barcode = "Kx7Pq2Zm"
        });

        var service = TicketTailorTestHost.CreateService(handler);
        var request = new IssueTicketRequest(
            EventId: "ev_test",
            TicketTypeId: "tt_type_001",
            HoldId: null,
            FullName: "Dana Brown",
            Email: "dana@example.com",
            SendEmail: false,
            ExternalReference: null);

        var result = await service.IssueTicketAsync(request);

        result.VendorTicketId.Should().Be("it_mapped");
        result.VendorOrderId.Should().BeNull();
        result.AttendeeName.Should().Be("Dana Brown");
        result.AttendeeEmail.Should().Be("dana@example.com");
        result.TicketTypeName.Should().Be("Weekend Pass");
        result.Price.Should().Be(200m);
        result.Status.Should().Be("valid");
        result.Barcode.Should().Be("Kx7Pq2Zm");
    }

    [HumansTheory]
    [InlineData(HttpStatusCode.BadRequest, TicketVendorFailureKind.Validation)]
    [InlineData(HttpStatusCode.UnprocessableEntity, TicketVendorFailureKind.Validation)]
    [InlineData(HttpStatusCode.Unauthorized, TicketVendorFailureKind.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, TicketVendorFailureKind.AuthFailed)]
    [InlineData(HttpStatusCode.NotFound, TicketVendorFailureKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, TicketVendorFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, TicketVendorFailureKind.Transient)]
    public async Task IssueTicketAsync_MapsStatusToFailureKind(HttpStatusCode status, TicketVendorFailureKind kind)
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(status, new { error = "sold out" });

        var service = TicketTailorTestHost.CreateService(handler);
        var request = new IssueTicketRequest(
            EventId: "ev_test",
            TicketTypeId: "tt_type_001",
            HoldId: null,
            FullName: "Test User",
            Email: null,
            SendEmail: false,
            ExternalReference: null);

        var act = () => service.IssueTicketAsync(request);

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(kind);
    }

    [HumansFact]
    public async Task IssueTicketAsync_TransportException_ThrowsTransient()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueThrow(new HttpRequestException("network failure"));

        var service = TicketTailorTestHost.CreateService(handler);
        var act = () => service.IssueTicketAsync(new IssueTicketRequest(
            EventId: "ev_test", TicketTypeId: "tt_1", HoldId: null,
            FullName: "Test User", Email: null, SendEmail: false, ExternalReference: null));

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.Transient);
        ex.Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [HumansFact]
    public async Task IssueTicketAsync_EmptyBody_ThrowsTransient()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, null);

        var service = TicketTailorTestHost.CreateService(handler);
        var act = () => service.IssueTicketAsync(new IssueTicketRequest(
            EventId: "ev_test", TicketTypeId: "tt_1", HoldId: null,
            FullName: "Test User", Email: null, SendEmail: false, ExternalReference: null));

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.Transient);
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IssueTicketAsync_UnusableId_ThrowsVendorFailure(string? id)
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new { id });
        var service = TicketTailorTestHost.CreateService(handler);

        var act = () => service.IssueTicketAsync(new IssueTicketRequest(
            EventId: null, TicketTypeId: null, HoldId: "hold_1",
            FullName: "Test User", Email: null, SendEmail: false, ExternalReference: null));

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.Transient);
        handler.Requests.Should().ContainSingle();
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\" \"")]
    [InlineData("\"voided\"")]
    [InlineData("\"unknown\"")]
    public async Task IssueTicketAsync_RequiresAnExplicitValidStatus(string? jsonStatus)
    {
        var document = JsonNode.Parse("""{"id":"it_new"}""")!;
        if (jsonStatus is not null) document["status"] = JsonNode.Parse(jsonStatus);
        var handler = new RecordingHttpHandler();
        var content = handler.EnqueueResponse(HttpStatusCode.Created, document);
        var service = TicketTailorTestHost.CreateService(handler);
        var issue = () => service.IssueTicketAsync(new IssueTicketRequest(
            EventId: null, TicketTypeId: null, HoldId: "hold_1",
            FullName: "Test User", Email: null, SendEmail: false, ExternalReference: null));

        var failure = await issue.Should().ThrowAsync<TicketVendorWriteException>();

        failure.Which.Kind.Should().Be(TicketVendorFailureKind.Transient);
        handler.RequestCount.Should().Be(1);
        content.WasDisposed.Should().BeTrue();
    }

    [HumansFact]
    public async Task IssueTicketAsync_MissingId_ThrowsVendorFailure()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new { });
        var service = TicketTailorTestHost.CreateService(handler);

        var act = () => service.IssueTicketAsync(new IssueTicketRequest(
            EventId: null, TicketTypeId: null, HoldId: "hold_1",
            FullName: "Test User", Email: null, SendEmail: false, ExternalReference: null));

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.Transient);
        handler.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task CreateCheckInAsync_PostsFormEncodedRequiredFields()
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new { id = "ci_1" });

        var service = TicketTailorTestHost.CreateService(handler);
        await service.CreateCheckInAsync("tt_1", Instant.FromUnixTimeSeconds(1751983320L), TestContext.Current.CancellationToken);

        var (request, body) = handler.Requests.Single();
        request.RequestUri!.ToString().Should().EndWith("/check_ins");
        request.Content!.Headers.ContentType!.MediaType.Should().Be("application/x-www-form-urlencoded");
        body.Should().Contain("issued_ticket_id=tt_1").And.Contain("quantity=1").And.Contain("check_in_at=1751983320");
    }

    [HumansTheory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CreateCheckInAsync_ThrowsRawHttpRequestExceptionOnFailure(HttpStatusCode status)
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(status, new { error = "check-in failed" });

        var service = TicketTailorTestHost.CreateService(handler);
        var act = () => service.CreateCheckInAsync("tt_1", Instant.FromUnixTimeSeconds(1751983320L), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.RequestCount.Should().Be(1);
    }

    [HumansTheory]
    [InlineData(DiscountType.Percentage, 25, "percentage", "price_percent", "25", false)]
    [InlineData(DiscountType.Percentage, 25, "percentage", "price_percent", "25", true)]
    [InlineData(DiscountType.Fixed, "25.25", "fixed_amount", "price", "2525", false)]
    [InlineData(DiscountType.Fixed, "25.25", "fixed_amount", "price", "2525", true)]
    public async Task GenerateDiscountCodesAsync_PostsDiscountFormsWithOptionalExpiry(
        DiscountType type, decimal value, string expectedType, string amountField, string expectedValue, bool withExpiry)
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.Created, new { code = "NOBO-ONE" });
        handler.EnqueueResponse(HttpStatusCode.Created, new { code = "NOBO-TWO" });
        Instant? expiry = withExpiry ? Instant.FromUnixTimeSeconds(1794000000) : null;
        using var culture = new CultureScope("es");

        var service = TicketTailorTestHost.CreateService(handler);
        var codes = await service.GenerateDiscountCodesAsync(
            new DiscountCodeSpec(Count: 2, DiscountType: type, DiscountValue: value, ExpiresAt: expiry),
            TestContext.Current.CancellationToken);

        codes.Should().Equal("NOBO-ONE", "NOBO-TWO");
        handler.Requests.Should().HaveCount(2);
        foreach (var (request, body) in handler.Requests)
        {
            request.RequestUri!.ToString().Should().EndWith("/discounts");
            request.Content!.Headers.ContentType!.MediaType.Should().Be("application/x-www-form-urlencoded");
            var fields = QueryHelpers.ParseQuery(body);
            fields["code"].ToString().Should().MatchRegex("^NOBO-[0-9A-F]{8}$");
            fields["name"].Should().Equal(fields["code"]);
            fields["type"].ToString().Should().Be(expectedType);
            fields[amountField].ToString().Should().Be(expectedValue);
            fields.Should().NotContainKey(type == DiscountType.Percentage ? "price" : "price_percent");
            if (withExpiry) fields["expires"].ToString().Should().Be("1794000000");
            else fields.Should().NotContainKey("expires");
        }
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task GenerateDiscountCodesAsync_RefusesSuccessfulResponseWithoutCode(string? code)
    {
        var handler = new RecordingHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.Created, new { code });
        var service = TicketTailorTestHost.CreateService(handler);

        var generate = () => service.GenerateDiscountCodesAsync(
            new DiscountCodeSpec(1, DiscountType.Percentage, 25m, null), TestContext.Current.CancellationToken);

        var failure = await generate.Should().ThrowAsync<HttpRequestException>();
        failure.Which.Message.Should().Contain("missing required code");
        handler.RequestCount.Should().Be(1);
    }
}
