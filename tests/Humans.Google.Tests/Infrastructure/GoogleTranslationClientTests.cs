using AwesomeAssertions;
using Humans.Google.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Google.Services.Workspace;

namespace Humans.Google.Tests.Infrastructure;

public sealed class GoogleTranslationClientTests
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task PublicContract_ResolvesInBothCredentialModesWithoutVendorAccess(bool hasCredentials)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GoogleWorkspace:ServiceAccountKeyPath"] = hasCredentials ? "/unused/service-account.json" : null
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new Section().Register(services, configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var translations = scope.ServiceProvider.GetRequiredService<IGoogleTranslationService>();

        translations.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<IGoogleTranslationService>());
        if (hasCredentials)
        {
            translations.Should().BeOfType<GoogleTranslationClient>();
            (await translations.TranslateAsync([], "en", "ca", Xunit.TestContext.Current.CancellationToken))
                .Should().BeEmpty();
        }
        else
        {
            (await translations.TranslateAsync(["sample"], "en", "ca", Xunit.TestContext.Current.CancellationToken))
                .Should().Equal("[ca] sample");
        }
    }

    [HumansTheory]
    [Xunit.InlineData("{}", "Translation returned no segments.")]
    [Xunit.InlineData("{\"data\":{\"translations\":null}}", "Translation returned no segments.")]
    [Xunit.InlineData("{\"data\":{\"translations\":[null]}}", "Translation returned a segment without translated text.")]
    [Xunit.InlineData("{\"data\":{\"translations\":[{}]}}", "Translation returned a segment without translated text.")]
    [Xunit.InlineData("{\"data\":{\"translations\":[{\"translatedText\":null}]}}", "Translation returned a segment without translated text.")]
    [Xunit.InlineData("{\"data\":{\"translations\":[]}}", "Translation returned 0 segments for 1 inputs.")]
    public async Task ReadTranslationsAsync_RejectsIncompleteBatches(string json, string expectedMessage)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var read = () => GoogleTranslationClient.ReadTranslationsAsync(content, 1, Xunit.TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage(expectedMessage);
    }

    [HumansFact]
    public async Task ReadTranslationsAsync_PreservesOrderUnicodeAndExplicitEmptyText()
    {
        using var content = new StringContent(
            """{"data":{"translations":[{"translatedText":"Bonjour, café"},{"translatedText":""}]}}""",
            System.Text.Encoding.UTF8, "application/json");

        var translations = await GoogleTranslationClient.ReadTranslationsAsync(content, 2, Xunit.TestContext.Current.CancellationToken);

        translations.Should().Equal("Bonjour, café", "");
    }
}
