using System.Net;
using AwesomeAssertions;
using Humans.Web.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Humans.Web.Tests.Authorization;

/// <summary>
/// The fallback authorization policy through the real pipeline: public surfaces stay public, everything else
/// challenges, and an unknown URL is still a 404 rather than a trip to the login page.
/// </summary>
public sealed class AnonymousRequestTests : IClassFixture<NoDatabaseFactory>
{
    private readonly HttpClient _client;

    public AnonymousRequestTests(NoDatabaseFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [HumansTheory]
    [InlineData("/health/live")]
    [InlineData("/api/version")]
    [InlineData("/Account/Login")]
    public async Task Public_endpoints_answer_a_signed_out_visitor(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [HumansTheory]
    [InlineData("/Admin")]
    [InlineData("/About/Staff")]
    public async Task Everything_else_challenges_a_signed_out_visitor(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.PathAndQuery.Should().StartWith("/Account/Login");
    }

    [HumansTheory]
    [InlineData("/no-such-page")]
    [InlineData("/assets/no-such-file.js")]
    public async Task Unknown_url_is_a_not_found_rather_than_a_challenge(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
