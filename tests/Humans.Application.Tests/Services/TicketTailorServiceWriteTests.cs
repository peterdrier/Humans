using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Humans.Application.Tests.Services;

public class TicketTailorServiceWriteTests
{
    private static TicketTailorService CreateService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = Options.Create(new TicketVendorSettings
        {
            EventId = "ev_test",
            SyncIntervalMinutes = 15,
            ApiKey = "test_key"
        });

        return new TicketTailorService(client, settings, cache,
            NullLogger<TicketTailorService>.Instance);
    }

    // ==========================================================================
    // VoidIssuedTicketAsync — happy path
    // ==========================================================================

    [HumansFact]
    public async Task VoidIssuedTicketAsync_VoidToHold_True_PostsCorrectFormBody()
    {
        string? capturedBody = null;
        var handler = new CapturingMockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "tt_xyz",
            hold_id = "hold_abc",
            voided = "yes"
        }, onReceive: async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
        });

        var service = CreateService(handler);
        var result = await service.VoidIssuedTicketAsync("tt_xyz", voidToHold: true);

        result.VendorTicketId.Should().Be("tt_xyz");
        result.HoldId.Should().Be("hold_abc");
        capturedBody.Should().Contain("void_to_hold=true");
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_VoidToHold_False_PostsFalseBody()
    {
        string? capturedBody = null;
        var handler = new CapturingMockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "tt_xyz",
            hold_id = (string?)null,
            voided = "yes"
        }, onReceive: async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
        });

        var service = CreateService(handler);
        await service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        capturedBody.Should().Contain("void_to_hold=false");
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_ResponseWithNoHoldId_ReturnsNullHoldId()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "tt_xyz",
            voided = "yes"
            // no hold_id field
        });

        var service = CreateService(handler);
        var result = await service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        result.VendorTicketId.Should().Be("tt_xyz");
        result.HoldId.Should().BeNull();
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_PostsToCorrectUrl()
    {
        string? capturedUrl = null;
        var handler = new CapturingMockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "tt_abc",
            voided = "yes"
        }, onReceive: req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return Task.CompletedTask;
        });

        var service = CreateService(handler);
        await service.VoidIssuedTicketAsync("tt_abc", voidToHold: true);

        capturedUrl.Should().Contain("/v1/issued_tickets/tt_abc/void");
    }

    // ==========================================================================
    // VoidIssuedTicketAsync — error status codes
    // ==========================================================================

    [HumansFact]
    public async Task VoidIssuedTicketAsync_400_ThrowsValidation()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.BadRequest, new { error = "bad request" });

        var service = CreateService(handler);
        var act = () => service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.Validation);
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_401_ThrowsAuthFailed()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.Unauthorized, new { error = "unauthorized" });

        var service = CreateService(handler);
        var act = () => service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.AuthFailed);
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_403_ThrowsAuthFailed()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.Forbidden, new { error = "forbidden" });

        var service = CreateService(handler);
        var act = () => service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.AuthFailed);
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_404_ThrowsNotFound()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.NotFound, new { error = "not found" });

        var service = CreateService(handler);
        var act = () => service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.NotFound);
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_429_ThrowsRateLimited()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.TooManyRequests, new { error = "rate limited" });

        var service = CreateService(handler);
        var act = () => service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.RateLimited);
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_500_ThrowsTransient()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, new { error = "server error" });

        var service = CreateService(handler);
        var act = () => service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.Transient);
    }

    [HumansFact]
    public async Task VoidIssuedTicketAsync_TransportException_ThrowsTransient()
    {
        var handler = new ThrowingHttpHandler(new HttpRequestException("network failure"));

        var service = CreateService(handler);
        var act = () => service.VoidIssuedTicketAsync("tt_xyz", voidToHold: false);

        var ex = await act.Should().ThrowAsync<TicketVendorWriteException>();
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.Transient);
        ex.Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ==========================================================================
    // IssueTicketAsync — argument validation
    // ==========================================================================

    [HumansFact]
    public async Task IssueTicketAsync_NeitherHoldNorEventAndType_ThrowsArgumentException()
    {
        var service = CreateService(new MockHttpHandler());
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
    }

    // ==========================================================================
    // IssueTicketAsync — happy path with HoldId
    // ==========================================================================

    [HumansFact]
    public async Task IssueTicketAsync_WithHoldId_PostsHoldIdFormKey()
    {
        string? capturedBody = null;
        var handler = new CapturingMockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "it_new",
            full_name = "Jane Doe",
            email = "jane@example.com",
            description = "Full Week",
            listed_price = 40000,
            status = "valid",
            order_id = (string?)null
        }, onReceive: async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
        });

        var service = CreateService(handler);
        var request = new IssueTicketRequest(
            EventId: null,
            TicketTypeId: null,
            HoldId: "hold_xyz",
            FullName: "Jane Doe",
            Email: "jane@example.com",
            SendEmail: false,
            ExternalReference: null);

        var result = await service.IssueTicketAsync(request);

        capturedBody.Should().Contain("hold_id=hold_xyz");
        capturedBody.Should().NotContain("event_id");
        capturedBody.Should().NotContain("ticket_type_id");
        result.VendorTicketId.Should().Be("it_new");
    }

    // ==========================================================================
    // IssueTicketAsync — happy path with EventId + TicketTypeId
    // ==========================================================================

    [HumansFact]
    public async Task IssueTicketAsync_WithEventAndType_PostsEventAndTypeFormKeys()
    {
        string? capturedBody = null;
        var handler = new CapturingMockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "it_new2",
            full_name = "Bob Smith",
            email = "bob@example.com",
            description = "Full Week",
            listed_price = 40000,
            status = "valid",
            order_id = (string?)null
        }, onReceive: async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
        });

        var service = CreateService(handler);
        var request = new IssueTicketRequest(
            EventId: "ev_test",
            TicketTypeId: "tt_type_001",
            HoldId: null,
            FullName: "Bob Smith",
            Email: "bob@example.com",
            SendEmail: false,
            ExternalReference: null);

        await service.IssueTicketAsync(request);

        capturedBody.Should().Contain("event_id=ev_test");
        capturedBody.Should().Contain("ticket_type_id=tt_type_001");
        capturedBody.Should().NotContain("hold_id");
    }

    [HumansFact]
    public async Task IssueTicketAsync_SetsFullNameAndSendEmailInBody()
    {
        string? capturedBody = null;
        var handler = new CapturingMockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "it_new3",
            full_name = "Carol White",
            email = "carol@example.com",
            description = "Full Week",
            listed_price = 40000,
            status = "valid",
            order_id = (string?)null
        }, onReceive: async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
        });

        var service = CreateService(handler);
        var request = new IssueTicketRequest(
            EventId: "ev_test",
            TicketTypeId: "tt_type_001",
            HoldId: null,
            FullName: "Carol White",
            Email: "carol@example.com",
            SendEmail: true,
            ExternalReference: "ref_abc");

        await service.IssueTicketAsync(request);

        capturedBody.Should().Contain("full_name=Carol+White");
        capturedBody.Should().Contain("send_email=true");
        capturedBody.Should().Contain("email=carol%40example.com");
        capturedBody.Should().Contain("reference=ref_abc");
    }

    [HumansFact]
    public async Task IssueTicketAsync_MapsResponseToVendorTicketDto()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new
        {
            id = "it_mapped",
            full_name = "Dana Brown",
            email = "dana@example.com",
            description = "Weekend Pass",
            listed_price = 20000,
            status = "valid",
            order_id = (string?)null
        });

        var service = CreateService(handler);
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
    }

    // ==========================================================================
    // IssueTicketAsync — error status codes
    // ==========================================================================

    [HumansFact]
    public async Task IssueTicketAsync_400_ThrowsValidation()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.BadRequest, new { error = "sold out" });

        var service = CreateService(handler);
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
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.Validation);
    }

    [HumansFact]
    public async Task IssueTicketAsync_500_ThrowsTransient()
    {
        var handler = new MockHttpHandler();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, new { error = "server error" });

        var service = CreateService(handler);
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
        ex.Which.Kind.Should().Be(TicketVendorFailureKind.Transient);
    }
}

/// <summary>
/// Mock handler that supports an optional per-response callback for inspecting
/// the outgoing request (URL, body, headers). Used by write-operation tests that
/// need to assert on the request payload.
/// </summary>
internal sealed class CapturingMockHttpHandler : HttpMessageHandler
{
    private readonly Queue<(HttpResponseMessage Response, Func<HttpRequestMessage, Task>? OnReceive)> _queue = new();

    public void EnqueueResponse(HttpStatusCode status, object body,
        Func<HttpRequestMessage, Task>? onReceive = null)
    {
        _queue.Enqueue((new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8,
                "application/json")
        }, onReceive));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var (response, onReceive) = _queue.Dequeue();
        if (onReceive is not null)
            await onReceive(request);
        return response;
    }
}

/// <summary>Always throws the given exception from SendAsync.</summary>
internal sealed class ThrowingHttpHandler(Exception exception) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
        => throw exception;
}
