using AwesomeAssertions;
using Humans.Auth.Services;
using Humans.Base.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Auth.Tests.Services;

public sealed class SignInLinkUrlBuilderTests
{
    // Real time: the framework protector checks expiry against the system clock.
    private readonly FakeClock _clock = new(SystemClock.Instance.GetCurrentInstant());

    private SignInLinkUrlBuilder Builder(IDataProtectionProvider provider) =>
        new(provider, Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" }), _clock);

    private static string TokenOf(string url) => QueryHelpers.ParseQuery(new Uri(url).Query)["token"].ToString();

    [HumansTheory]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(true, "")]
    [InlineData(false, "")]
    [InlineData(true, "%")]
    [InlineData(false, "%")]
    [InlineData(true, "a")]
    [InlineData(false, "a")]
    public void Unprotect_MalformedToken_IsInvalid(bool login, string? token)
    {
        var builder = Builder(new EphemeralDataProtectionProvider());

        var payload = login
            ? builder.UnprotectSignInToken(token!)
            : builder.UnprotectSignupToken(token!);

        payload.Should().BeNull();
    }

    [HumansFact]
    public void Unprotect_GeneratedToken_ResolvesPayload()
    {
        var builder = Builder(new EphemeralDataProtectionProvider());
        var userId = Guid.NewGuid();
        var expiresAt = _clock.GetCurrentInstant() + Duration.FromMinutes(15);

        builder.UnprotectSignInToken(TokenOf(builder.BuildSignInUrl(userId, null)))
            .Should().Be(new SignInLinkToken(userId.ToString(), expiresAt));
        builder.UnprotectSignupToken(TokenOf(builder.BuildSignupUrl("member@example.com", null)))
            .Should().Be(new SignInLinkToken("member@example.com", expiresAt));
    }

    [HumansFact]
    public void Unprotect_ExpiredToken_StillDecodes()
    {
        var builder = Builder(new EphemeralDataProtectionProvider());
        var token = TokenOf(builder.BuildSignupUrl("member@example.com", null));
        _clock.Advance(Duration.FromHours(1));

        builder.UnprotectSignupToken(token)!.Payload.Should().Be("member@example.com");
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unprotect_FrameworkToken_StillRedeems(bool login)
    {
        // Links already in inboxes at deploy were issued by ToTimeLimitedDataProtector.
        var provider = new EphemeralDataProtectionProvider();
        var builder = Builder(provider);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        var token = provider.CreateProtector(login ? "MagicLinkLogin" : "MagicLinkSignup")
            .ToTimeLimitedDataProtector()
            .Protect("payload", expiresAt);

        var decoded = login ? builder.UnprotectSignInToken(token) : builder.UnprotectSignupToken(token);

        decoded.Should().Be(new SignInLinkToken("payload", Instant.FromDateTimeOffset(expiresAt)));
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unprotect_AlternateEncoding_IsInvalid(bool login)
    {
        var provider = new EphemeralDataProtectionProvider();
        var builder = Builder(provider);
        var url = login
            ? builder.BuildSignInUrl(Guid.NewGuid(), null)
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
            ? builder.UnprotectSignInToken(alternate)
            : builder.UnprotectSignupToken(alternate);

        payload.Should().BeNull();
    }
}
