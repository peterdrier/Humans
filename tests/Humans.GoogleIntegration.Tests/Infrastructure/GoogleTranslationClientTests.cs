using AwesomeAssertions;
using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Tests.Infrastructure;

public sealed class GoogleTranslationClientTests
{
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
