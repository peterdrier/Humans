using System.Net;
using AwesomeAssertions;
using Humans.Stripe.Services;
using NSubstitute;
// Compilation-unit level, so this binds to the SDK's global::Stripe; see StripeService.cs.
using Stripe;

namespace Humans.Stripe.Tests.Mirror;

public sealed class StripeMirrorClientTests
{
    private readonly IHttpClient _http = Substitute.For<IHttpClient>();
    private readonly List<StripeRequest> _requests = [];

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private const string Charge = """
        {"id":"txn_c","object":"balance_transaction","amount":31500,"available_on":1783555200,"created":1783353748,
         "currency":"eur","description":"Order for Elsewhere","fee":498,"net":31002,"reporting_category":"charge",
         "status":"available","type":"payment",
         "source":{"id":"py_1","object":"charge","payment_intent":"pi_1","receipt_email":null,
           "billing_details":{"email":"buyer@example.org","name":null},
           "payment_method_details":{"type":"card","card":{"last4":"4242"}},
           "metadata":{"name":"Ana Buyer","order_id":"79159110"}}}
        """;

    private const string Payout = """
        {"id":"txn_p","object":"balance_transaction","amount":-31002,"available_on":1784564011,"created":1784564011,
         "currency":"eur","fee":0,"net":-31002,"reporting_category":"payout","status":"available","type":"payout",
         "source":{"id":"po_1","object":"payout","amount":31002,"currency":"eur","status":"paid","automatic":true}}
        """;

    private void Respond(Func<StripeRequest, string> body)
    {
        using var message = new HttpResponseMessage();
        var headers = message.Headers;
        _http.MakeRequestAsync(Arg.Any<StripeRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<StripeRequest>();
            _requests.Add(request);
            return new StripeResponse(HttpStatusCode.OK, headers, body(request));
        });
    }

    private static string List(bool hasMore, params string[] items) =>
        $$"""{"object":"list","has_more":{{(hasMore ? "true" : "false")}},"data":[{{string.Join(",", items)}}]}""";

    private static string Route(StripeRequest r)
    {
        var path = r.Uri.AbsolutePath;
        var query = Uri.UnescapeDataString(r.Uri.Query);
        if (path.EndsWith("/balance", StringComparison.Ordinal))
            return """{"object":"balance","available":[{"amount":0,"currency":"eur"}],"pending":[{"amount":0,"currency":"eur"}]}""";
        if (path.EndsWith("/payouts", StringComparison.Ordinal))
            return List(false,
                """{"id":"po_1","object":"payout","amount":31002,"currency":"eur","status":"paid","automatic":true,"reconciliation_status":"completed","arrival_date":1784592000,"created":1784564011}""",
                // A split payout's secondary part: Stripe answers 400 to a breakdown request for it.
                """{"id":"po_2","object":"payout","amount":5,"currency":"eur","status":"paid","automatic":true,"reconciliation_status":"not_applicable","arrival_date":1784592000,"created":1784564011}""");
        if (query.Contains("payout=po_1", StringComparison.Ordinal))
            return List(false, """{"id":"txn_c","object":"balance_transaction"}""", """{"id":"txn_p","object":"balance_transaction"}""");
        return query.Contains("starting_after=txn_c", StringComparison.Ordinal) ? List(false, Payout) : List(true, Charge);
    }

    [HumansFact]
    public async Task Org_key_reads_every_page_through_Stripe_Context_and_links_automatic_payouts()
    {
        Respond(Route);
        var account = new StripeMirrorAccount { Name = "Tickets", Id = "acct_t", ApiKey = "sk_org_test" };

        var fetch = await new StripeMirrorClient(_http).FetchAsync(account, Ct);

        _requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        _requests.Should().OnlyContain(r => string.Equals(r.StripeHeaders["Stripe-Context"], "acct_t", StringComparison.Ordinal));
        _requests.Should().OnlyContain(r => !string.IsNullOrEmpty(r.StripeHeaders["Stripe-Version"]));
        fetch.Transactions.Select(t => t.Id).Should().Equal("txn_c", "txn_p");

        var charge = fetch.Transactions[0];
        charge.Should().Match<Domain.StripeBalanceTransaction>(t =>
            t.Amount == 31500 && t.Fee == 498 && t.Net == 31002);
        new[] { charge.AccountId, charge.Currency, charge.SourceId, charge.PaymentIntentId, charge.PayoutId, charge.CustomerName, charge.CustomerEmail }
            .Should().Equal("acct_t", "EUR", "py_1", "pi_1", "po_1", "Ana Buyer", "buyer@example.org");
        charge.Metadata.Should().Contain("79159110");
        charge.Raw.Should().Contain("\"source\":\"py_1\"").And.NotContain("4242", "card details are not mirrored");
        fetch.Transactions[1].PayoutId.Should().Be("po_1");
        fetch.Payouts.Select(p => p.Id).Should().Equal("po_1", "po_2");
        _requests.Should().NotContain(r => r.Uri.Query.Contains("po_2", StringComparison.Ordinal),
            "a non-reconcilable payout is mirrored but never broken down");
        fetch.Balances.Single().Currency.Should().Be("EUR");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Bounded_mirror_fields_preserve_complete_unicode_characters(bool pairFits)
    {
        var namePrefix = new string('n', pairFits ? 254 : 255);
        var descriptionPrefix = new string('d', pairFits ? 998 : 999);
        var name = System.Text.Json.JsonSerializer.Serialize(namePrefix + "😀tail");
        var description = System.Text.Json.JsonSerializer.Serialize(descriptionPrefix + "😀tail");
        Respond(r => Route(r)
            .Replace("\"Order for Elsewhere\"", description, StringComparison.Ordinal)
            .Replace("\"Ana Buyer\"", name, StringComparison.Ordinal)
            .Replace("\"buyer@example.org\"", name, StringComparison.Ordinal)
            .Replace("\"reconciliation_status\":\"completed\"",
                "\"reconciliation_status\":\"completed\",\"failure_message\":" + description, StringComparison.Ordinal));
        var account = new StripeMirrorAccount { Name = "Tickets", Id = "acct_t", ApiKey = "sk_org_test" };

        var fetch = await new StripeMirrorClient(_http).FetchAsync(account, Ct);

        var charge = fetch.Transactions[0];
        var suffix = pairFits ? "😀" : "";
        charge.CustomerName.Should().Be(namePrefix + suffix);
        charge.CustomerEmail.Should().Be(namePrefix + suffix);
        charge.Description.Should().Be(descriptionPrefix + suffix);
        fetch.Payouts[0].FailureMessage.Should().Be(descriptionPrefix + suffix);
        var encoder = new System.Text.UTF8Encoding(false, true);
        foreach (var value in new[] { charge.CustomerName, charge.CustomerEmail, charge.Description, fetch.Payouts[0].FailureMessage })
        {
            var encode = () => encoder.GetBytes(value!);
            encode.Should().NotThrow();
        }
    }

    [HumansFact]
    public async Task An_account_with_its_own_key_sends_no_Stripe_Context()
    {
        Respond(Route);
        var account = new StripeMirrorAccount { Name = "ICE", Id = "acct_i", KeyVariable = "STRIPE_ICE_READ_KEY", ApiKey = "rk_live_ice" };

        await new StripeMirrorClient(_http).FetchAsync(account, Ct);

        _requests.Should().NotBeEmpty().And.OnlyContain(r => !r.StripeHeaders.ContainsKey("Stripe-Context"));
    }

    [HumansTheory]
    [Xunit.InlineData("transactions")]
    [Xunit.InlineData("payouts")]
    [Xunit.InlineData("payout-breakdown")]
    public async Task Missing_completion_metadata_cannot_publish_a_partial_fetch(string consumer)
    {
        var targetCalls = 0;
        Respond(r =>
        {
            var path = r.Uri.AbsolutePath;
            var query = Uri.UnescapeDataString(r.Uri.Query);
            var isTarget = consumer switch
            {
                "payouts" => path.EndsWith("/payouts", StringComparison.Ordinal),
                "payout-breakdown" => query.Contains("payout=po_1", StringComparison.Ordinal),
                _ => path.EndsWith("/balance_transactions", StringComparison.Ordinal)
                    && !query.Contains("payout=", StringComparison.Ordinal),
            };
            if (!isTarget) return Route(r);
            return ++targetCalls == 1
                ? Route(r).Replace("\"has_more\":false", "\"has_more\":true", StringComparison.Ordinal)
                : """{"object":"list","data":[]}""";
        });
        var account = new StripeMirrorAccount { Name = "Tickets", Id = "acct_t", ApiKey = "sk_org_test" };

        var act = () => new StripeMirrorClient(_http).FetchAsync(account, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*boolean has_more*");
        targetCalls.Should().Be(2);
    }

    [HumansFact]
    public async Task A_pager_that_never_ends_throws_instead_of_returning_a_prefix()
    {
        Respond(r => r.Uri.AbsolutePath.EndsWith("/balance_transactions", StringComparison.Ordinal) ? List(true) : List(false));
        var account = new StripeMirrorAccount { Name = "Tickets", Id = "acct_t", ApiKey = "sk_org_test" };

        var act = () => new StripeMirrorClient(_http).FetchAsync(account, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*has_more on an empty page*");
    }
}
