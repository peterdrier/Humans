using AwesomeAssertions;
using Humans.Auth.Services;
using Humans.Base.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Auth.Tests.Services;

public sealed class MagicLinkUrlBuilderTests
{
    // Real time: the framework protector checks expiry against the system clock.
    private readonly FakeClock _clock = new(SystemClock.Instance.GetCurrentInstant());

    private MagicLinkUrlBuilder Builder(IDataProtectionProvider provider, IKeyManager? keys = null) =>
        new(provider, keys ?? Substitute.For<IKeyManager>(), Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" }), _clock);

    private static ServiceProvider KeyRingServices() =>
        new ServiceCollection().AddDataProtection()
            .PersistKeysToFileSystem(Directory.CreateTempSubdirectory("magic-link-keys"))
            .Services.BuildServiceProvider();

    private MagicLinkUrlBuilder KeyRingBuilder(ServiceProvider services) =>
        Builder(services.GetRequiredService<IDataProtectionProvider>(), services.GetRequiredService<IKeyManager>());

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
    public void Missing_or_malformed_token_is_invalid(bool login, string? token)
    {
        var builder = Builder(new EphemeralDataProtectionProvider());

        var payload = login
            ? builder.UnprotectLoginToken(token!)
            : builder.UnprotectSignupToken(token!);

        payload.Token.Should().BeNull();
    }

    [HumansFact]
    public void Generated_tokens_resolve_their_payloads_and_expiry()
    {
        var builder = Builder(new EphemeralDataProtectionProvider());
        var userId = Guid.NewGuid();
        var expiresAt = _clock.GetCurrentInstant() + Duration.FromMinutes(15);

        builder.UnprotectLoginToken(TokenOf(builder.BuildLoginUrl(userId, null))).Token
            .Should().Be(new MagicLinkToken(userId.ToString(), expiresAt));
        builder.UnprotectSignupToken(TokenOf(builder.BuildSignupUrl("member@example.com", null))).Token
            .Should().Be(new MagicLinkToken("member@example.com", expiresAt));
    }

    [HumansFact]
    public void Expired_token_still_decodes_so_it_can_be_told_apart_from_a_forged_one()
    {
        var builder = Builder(new EphemeralDataProtectionProvider());
        var token = TokenOf(builder.BuildSignupUrl("member@example.com", null));
        _clock.Advance(Duration.FromHours(1));

        builder.UnprotectSignupToken(token).Token!.Payload.Should().Be("member@example.com");
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tokens_issued_by_the_framework_time_limited_protector_still_redeem(bool login)
    {
        // Links already in inboxes at deploy were issued by ToTimeLimitedDataProtector.
        var provider = new EphemeralDataProtectionProvider();
        var builder = Builder(provider);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        var token = provider.CreateProtector(login ? "MagicLinkLogin" : "MagicLinkSignup")
            .ToTimeLimitedDataProtector()
            .Protect("payload", expiresAt);

        var decoded = login ? builder.UnprotectLoginToken(token) : builder.UnprotectSignupToken(token);

        decoded.Token.Should().Be(new MagicLinkToken("payload", Instant.FromDateTimeOffset(expiresAt)));
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Alternate_encoding_of_a_valid_token_is_invalid(bool login)
    {
        var provider = new EphemeralDataProtectionProvider();
        var builder = Builder(provider);
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

        payload.Token.Should().BeNull();
    }

    [HumansFact]
    public void An_unreadable_token_says_which_of_its_checks_failed()
    {
        using var services = KeyRingServices();
        var builder = KeyRingBuilder(services);
        var signup = TokenOf(builder.BuildSignupUrl("member@example.com", null));
        var bytes = WebEncoders.Base64UrlDecode(signup);
        bytes[^1] ^= 1;
        var foreign = TokenOf(Builder(new EphemeralDataProtectionProvider()).BuildSignupUrl("member@example.com", null));

        builder.UnprotectLoginToken(signup).Problem.Should().Be("token is a signup link's");
        builder.UnprotectSignupToken(WebEncoders.Base64UrlEncode(bytes)).Problem.Should().Be("token fails its integrity check: it was altered");
        builder.UnprotectSignupToken(foreign).Problem.Should().StartWith("token was signed with key ").And.EndWith(", which is not in the key ring");
        builder.UnprotectSignupToken(WebEncoders.Base64UrlEncode([1, 2, 3])).Problem.Should().Be("token is not a Data Protection payload");
    }
}
