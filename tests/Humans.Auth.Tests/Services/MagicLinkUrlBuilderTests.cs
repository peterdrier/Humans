using AwesomeAssertions;
using Humans.Auth.Services;
using Humans.Base.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Humans.Auth.Tests.Services;

public sealed class MagicLinkUrlBuilderTests
{
    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Missing_token_is_invalid(bool login)
    {
        var builder = new MagicLinkUrlBuilder(
            new EphemeralDataProtectionProvider(),
            Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" }),
            NullLogger<MagicLinkUrlBuilder>.Instance);

        var payload = login
            ? builder.UnprotectLoginToken(null!)
            : builder.UnprotectSignupToken(null!);

        payload.Should().BeNull();
    }

    [HumansFact]
    public void Generated_tokens_still_resolve_their_payloads()
    {
        var builder = new MagicLinkUrlBuilder(
            new EphemeralDataProtectionProvider(),
            Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" }),
            NullLogger<MagicLinkUrlBuilder>.Instance);
        var userId = Guid.NewGuid();
        var loginToken = QueryHelpers.ParseQuery(new Uri(builder.BuildLoginUrl(userId, null)).Query)["token"].ToString();
        var signupToken = QueryHelpers.ParseQuery(new Uri(builder.BuildSignupUrl("member@example.com", null)).Query)["token"].ToString();

        builder.UnprotectLoginToken(loginToken).Should().Be(userId.ToString());
        builder.UnprotectSignupToken(signupToken).Should().Be("member@example.com");
    }
}
