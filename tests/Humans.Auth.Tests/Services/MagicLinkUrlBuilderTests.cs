using AwesomeAssertions;
using Humans.Auth.Services;
using Humans.Base.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Xunit;

namespace Humans.Auth.Tests.Services;

public sealed class MagicLinkUrlBuilderTests
{
    [HumansTheory]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(true, "")]
    [InlineData(false, "")]
    [InlineData(true, "%")]
    [InlineData(false, "%")]
    [InlineData(true, "a")]
    [InlineData(false, "a")]
    public void Missing_or_malformed_token_is_invalid(bool login, string? token)
    {
        var builder = new MagicLinkUrlBuilder(
            new EphemeralDataProtectionProvider(),
            Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" }));

        var payload = login
            ? builder.UnprotectLoginToken(token!)
            : builder.UnprotectSignupToken(token!);

        payload.Should().BeNull();
    }

    [HumansFact]
    public void Generated_tokens_still_resolve_their_payloads()
    {
        var builder = new MagicLinkUrlBuilder(
            new EphemeralDataProtectionProvider(),
            Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" }));
        var userId = Guid.NewGuid();
        var loginToken = QueryHelpers.ParseQuery(new Uri(builder.BuildLoginUrl(userId, null)).Query)["token"].ToString();
        var signupToken = QueryHelpers.ParseQuery(new Uri(builder.BuildSignupUrl("member@example.com", null)).Query)["token"].ToString();

        builder.UnprotectLoginToken(loginToken).Should().Be(userId.ToString());
        builder.UnprotectSignupToken(signupToken).Should().Be("member@example.com");
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Alternate_encoding_of_a_valid_token_is_invalid(bool login)
    {
        var provider = new EphemeralDataProtectionProvider();
        var builder = new MagicLinkUrlBuilder(provider,
            Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" }));
        var url = login
            ? builder.BuildLoginUrl(Guid.NewGuid(), null)
            : builder.BuildSignupUrl("member@example.com", null);
        var token = QueryHelpers.ParseQuery(new Uri(url).Query)["token"].ToString();
        // Base64 decoding ignores whitespace. The protected bytes and payload are
        // unchanged, but a replay cache keyed on token text sees a different token.
        var alternate = token.Insert(4, "    ");
        WebEncoders.Base64UrlDecode(alternate).Should().Equal(WebEncoders.Base64UrlDecode(token));
        var protector = provider.CreateProtector(login ? "MagicLinkLogin" : "MagicLinkSignup")
            .ToTimeLimitedDataProtector();
        protector.Unprotect(alternate).Should().Be(protector.Unprotect(token));

        var payload = login
            ? builder.UnprotectLoginToken(alternate)
            : builder.UnprotectSignupToken(alternate);

        payload.Should().BeNull();
    }
}
