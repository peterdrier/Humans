using System.Net;
using System.Text;
using System.Text.Json;
using Humans.Pretix.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Humans.Stripe.Contracts;
using NSubstitute;

namespace Humans.Pretix.Tests;

internal static class PretixTestHost
{
    public const string Api = "/api/v1/organizers/nobodies/events/nowhere2027/";

    public static PretixSettings Settings() => new()
    {
        BaseUrl = "https://pretix.test/",
        Organizer = "nobodies",
        ApiToken = "tok",
        WebhookSecret = "s3cret",
    };

    /// <summary>Over <paramref name="stripe"/>, or a Stripe section with no account configured for pretix.</summary>
    public static PretixCheckoutBackend Create(FakePretix pretix, PretixSettings? settings = null, IStripeService? stripe = null) =>
        new(new HttpClient(pretix), Options.Create(settings ?? Settings()), NullLogger<PretixCheckoutBackend>.Instance,
            stripe ?? NoStripeAccount());

    public static IStripeService NoStripeAccount()
    {
        var stripe = Substitute.For<IStripeService>();
        stripe.CheckAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        return stripe;
    }

    public static object Order(
        string code,
        string status = "n",
        decimal total = 1000m,
        object[]? positions = null,
        object[]? fees = null,
        object[]? payments = null,
        object[]? refunds = null,
        object? apiMeta = null) => new
        {
            code,
            status,
            url = $"https://pretix.test/nobodies/nowhere2027/order/{code}/secret/",
            total = total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            positions = positions ?? [Position(1, "225.00", "21.00"), Position(2, "775.00", "0.00")],
            fees = fees ?? [],
            payments = payments ?? [],
            refunds = refunds ?? [],
            api_meta = apiMeta ?? new { },
        };

    public static object Position(int id, string price, string taxRate) =>
        new { positionid = id, price, tax_rate = taxRate, canceled = false };
}

/// <summary>Answers by method and path from queued responses, and records every request it saw.</summary>
internal sealed class FakePretix : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _routes = new(StringComparer.Ordinal);

    public List<(string Route, string? Body)> Requests { get; } = [];
    public Exception? Failure { get; init; }

    public FakePretix On(string method, string path, HttpStatusCode status, object body)
    {
        var key = $"{method} {path}";
        if (!_routes.TryGetValue(key, out var queue))
            _routes[key] = queue = new Queue<(HttpStatusCode, string)>();
        queue.Enqueue((status, body as string ?? JsonSerializer.Serialize(body)));
        return this;
    }

    public IEnumerable<string?> BodiesFor(string method, string path) =>
        Requests.Where(r => string.Equals(r.Route, $"{method} {path}", StringComparison.Ordinal)).Select(r => r.Body);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Failure is { } failure) throw failure;
        var route = $"{request.Method} {request.RequestUri!.AbsolutePath}";
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((route, body));
        if (!_routes.TryGetValue(route, out var queue) || queue.Count == 0)
            throw new InvalidOperationException($"Unexpected pretix call {route}");
        var (status, payload) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        return new HttpResponseMessage(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    }
}
